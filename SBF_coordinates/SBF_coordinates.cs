﻿////////////////////////////////////////////////////////////////////////////////
// 
//  ESAPI V15.5 single plugin script to get SBF coordinates from plan
//  
//
// Copyright (c) 2020 
//
// Permission is hereby granted, free of charge, to any person obtaining a copy 
// of this software and associated documentation files (the "Software"), to deal 
// in the Software without restriction, including without limitation the rights 
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
// copies of the Software, and to permit persons to whom the Software is 
// furnished to do so, subject to the following conditions:
//
//  The above copyright notice and this permission notice shall be included in 
//  all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL 
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, 
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN 
// THE SOFTWARE.
////////////////////////////////////////////////////////////////////////////////
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Runtime.CompilerServices;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;



/* * 
 * TODO: the person that created this plan should NOT run the script, and should be physicist, and part of the SBRT group.
 * OR Only in status planning approved?, members of the SBRT group might change... OR if done in WPF; user input of measured coordinates
 * hard to check if the documents with coordinates exists but possible
 * 
 * Patient/image orientation:
 * LAT: methods work regardless, only when calculating the final SBRT coordinate value has this to be taken into consideration
 * VRT: works for HFS and FFS, lot of checking to do if this is going to work for HFP and FFP... need to pass on planorientation and/or imageorientation
 * LNG: Measures directly in the frame, need to take care of this when working with relative distances in dicom coordinates
 * 
 * Start by getting frame of reference for the SBF in Lat and Vrt in the Lng-coordinate specified by the point of interest (user origo, isocenter or SBF setup marker)
 * Two use cases1: 
 * 1: planSetup on planning CT, check origo, iso and SBF setup 
 * 2: planSetup on 4DCT phase, might mean that the check of the SBF setup coordinates have to be done on a separate image without plan.
 * 
 * Double checking:
 * Vrt coordinate is double checked by taking a lateral profile 10 mm above the SBF bottom, i.e. where the frame widens, and comparing the found width of the SBF with the expected value.
 * Lat coordinate is double checked simply by comparing the found width of the SBF with the expected value.
 * Long is compared left side to right side, and is the parameter most likely to fail for profile measurement, depending on image quality, slice thickness, wall flex and fidusle condition.
 * */




namespace VMS.TPS
{
    public class Script
    {
        public Script()
        {
        }

        enum CheckResults
        {
            Found,
            NoLong,
            NotFound,
            NotOK
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Execute(ScriptContext context /*, System.Windows.Window window, ScriptEnvironment environment*/)
        {
            if (context.Patient == null)
            {
                MessageBox.Show("Please select a patient and plan/image in active context window.");
            }
            else if (context.PlanSetup == null && context.StructureSet.Image != null)
            {
                // (image.ImagingOrientation.Equals("HeadFirstSupine") || image.ImagingOrientation.Equals("FeetFirstSupine"))
                Image image = context.StructureSet.Image;
                if (image.ImagingOrientation == PatientOrientation.HeadFirstSupine || image.ImagingOrientation == PatientOrientation.FeetFirstSupine ) 
                {
                    var OrigoCheckResults = CheckResults.NotFound;
                    int origoLong = 0;
                    if (image.HasUserOrigin)
                    {
                        string checkSBRTOrigo = CheckUserOriginInSBRTFrame(image, ref OrigoCheckResults, ref origoLong);  // should really refactor this
                        MessageBox.Show(checkSBRTOrigo, "User Origin Check");
                    }
                    string checkSBFsetup = GetSBFSetupCoord(context.StructureSet, OrigoCheckResults, origoLong);
                    MessageBox.Show(checkSBFsetup, "SBF set-up marker Check");
                }
                else
                {
                    MessageBox.Show("Sorry! This script can only handle patient orientation HFS and FFS.");
                }
            }
            else if (context.PlanSetup != null && context.PlanSetup.StructureSet.Image != null)
            {
                PlanSetup plan = context.PlanSetup;
                Image image = plan.StructureSet.Image;
                if ((image.ImagingOrientation == PatientOrientation.HeadFirstSupine || image.ImagingOrientation == PatientOrientation.FeetFirstSupine) && (plan.TreatmentOrientation == PatientOrientation.HeadFirstSupine || plan.TreatmentOrientation == PatientOrientation.FeetFirstSupine))
                {
                    int origoLong = 0;

                    var OrigoCheckResults = CheckResults.NotFound;
                    string warningString = "Warning: SCRIPT NOT EVALUATED OR APPROVED FOR CLINICAL USE!\n\n";
                    string checkSBRTOrigo = warningString + CheckUserOriginInSBRTFrame(image, ref OrigoCheckResults, ref origoLong);  // should really refactor this
                    string checkSBRTiso = warningString + GetIsoCoordInSBRTFrame(plan, OrigoCheckResults, origoLong);
                    string checkSBFsetup = warningString + GetSBFSetupCoord(plan.StructureSet, OrigoCheckResults, origoLong);

                    MessageBox.Show(checkSBRTOrigo, "User Origin Check");
                    MessageBox.Show(checkSBRTiso, "Isocenter Check on plan " + plan.Id);
                    MessageBox.Show(checkSBFsetup, "SBF set-up marker Check");
                    // TODO: possible to check 3 different points in space for agreement between measured and calcolated with delta
                }
                else
                {
                    MessageBox.Show("Sorry! This script can only handle patient orientation HFS and FFS.");
                }
            }
            else
            {
                MessageBox.Show("Please select a patient and plan/image in active context window.");
            }
        }

        //*********HELPER METHODS**************
        /// <summary>
        /// SameSign; helper method to determine if two doubles have the same sign
        /// </summary>
        /// <param name="num1"></param>
        /// <param name="num2"></param>
        /// <returns></returns>
        bool SameSign(double num1, double num2)
        {
            return num1 >= 0 && num2 >= 0 || num1 < 0 && num2 < 0;
        }

        public class PatternGradient
        {
            public List<double> DistanceInMm { get; set; }
            public List<int> GradientHUPerMm { get; set; }
            public List<double> MinGradientLength { get; set; }
            public List<int> PositionToleranceMm { get; set; }
            public int GradIndexForCoord { get; set; }
        }


        /// <summary>
        /// User origin position is used to double check coordinates of isocenter and SBF setup marker
        /// </summary>
        /// <param name="plan"></param>
        /// <returns></returns>
        private string CheckUserOriginInSBRTFrame(Image image, ref CheckResults origoCheckResults, ref int origoLong)
        {
            string userOrigoCheck = "";

            double bottom;
            double left;                // left and right side to pass it on to method to get longcoordinates
            double right;               // might not be actual left or right, simply a way to denote the two different sides
            int positionTolerance = 3;  // Tolerance for accepting the position of user origo and returning coordinates based on set user origo for comparison

            var coord = GetTransverseCoordInSBRTFrame(image, image.UserOrigin);
            left = coord[0];
            right = coord[1];
            bottom = coord[2];
            double lateralCenterSBRT = (left + right) / 2; // Dicom position of the lateral center of the SBF

            double coordSRSLong = GetSRSLongCoord(image, image.UserOrigin, (int)Math.Round(bottom), left, right);

            int userOrigoLatSRS = (int)(Math.Round(lateralCenterSBRT + 300 - image.UserOrigin.x));      // TODO: this works for HFS and FFS. HFP and FFP unhandled
            int userOrigoVrtSRS = (int)(Math.Round(bottom - image.UserOrigin.y));                       // TODO: this works for HFS and FFS. HFP and FFP unhandled
            int userOrigoLongSRS = (int)Math.Round(coordSRSLong);

            if (bottom == 0)
            {
                userOrigoCheck += "Cannot find the SBF in user origin plane, no automatic check of User origo possible.";
                origoCheckResults = CheckResults.NotFound;
            }
            else if (userOrigoLongSRS == 0 && userOrigoVrtSRS < (95 + positionTolerance) && userOrigoVrtSRS > (95 - positionTolerance))
            {
                userOrigoCheck += "Cannot find the SBF long coordinate of the user origin with profiles. Estimated position of origo in SBRT frame coordinates in transverse direction from image profiles: \n\n" +
                " Lat: " + userOrigoLatSRS + "\t Vrt: " + userOrigoVrtSRS + "\n";
                origoCheckResults = CheckResults.NoLong;
            }
            else if (userOrigoVrtSRS < (95 + positionTolerance) && userOrigoVrtSRS > (95 - positionTolerance) && Math.Abs(image.UserOrigin.x - lateralCenterSBRT) < positionTolerance)
            {
                userOrigoCheck += "Estimated position of user origin in SBF coordinates from image profiles: \n\n" +
                " Lat: " + userOrigoLatSRS + "\t Vrt: " + userOrigoVrtSRS + "\t Lng: " + userOrigoLongSRS + "\t (+/- 3mm)" +
                "\n";
                origoCheckResults = CheckResults.Found;
                origoLong = userOrigoLongSRS;
            }
            else
            {
                userOrigoCheck += "* Check the position of user origin.";
                origoCheckResults = CheckResults.NotOK;
            }
            return userOrigoCheck;
        }


        
        private string GetIsoCoordInSBRTFrame(PlanSetup plan, CheckResults origoCheckResults, int origoLong)
        {
            string isoSBRTresults = "";

            var image = plan.StructureSet.Image;
            var iso = plan.Beams.First().IsocenterPosition; // assumes that check have been done that all beams have same isocenter
            double bottom;
            double left;
            double right;

            var coord = GetTransverseCoordInSBRTFrame(image, plan.Beams.First().IsocenterPosition);
            left = coord[0];
            right = coord[1];
            bottom = coord[2];
            double lateralCenterSBRT = (left + right) / 2;
            double coordSRSLong = GetSRSLongCoord(image, plan.Beams.First().IsocenterPosition, (int)Math.Round(bottom), left, right);

            //  Calculate the final values in SBRT frame coordinates TODO: this works for HFS and FFS. HFP and FFP unhandled

            int isoLatSBRT = (int)(Math.Round(lateralCenterSBRT + 300 - iso.x));
            int isoVrtSBRT = (int)(Math.Round(bottom - iso.y));
            int isoLongSRS = (int)Math.Round(coordSRSLong);

            // Get Vrt and Lat calculated directly from user origo (dicom coordinate for transverse and found long for long) for comparison if check of user origo ok

            int isoLatSBRTFromUO = (int)(Math.Round(-(iso.x - image.UserOrigin.x - 300)));         // TODO: this works for HFS and FFS. HFP and FFP unhandled
            int isoVrtSBRTFromUO = (int)(Math.Round(Math.Abs(iso.y - image.UserOrigin.y - 95)));   // TODO: this works for HFS and FFS. HFP and FFP unhandled
            int isoLngSBRTFromUO = (int)(Math.Round(Math.Abs(iso.z - image.UserOrigin.z + origoLong))); // TODO: this works for HFS and FFS. HFP and FFP unhandled


            var isoCheckResults = CheckResults.NotFound;
            string measuredIsoCoord = "";

            if (bottom == 0)    // bottom set to 0 if bottom OR lat not found
            {
                isoCheckResults = CheckResults.NotFound;
            }
            else if (isoLongSRS == 0)
            {
                isoCheckResults = CheckResults.NoLong;
                measuredIsoCoord = " Lat: " + isoLatSBRT + "\t Vrt: " + isoVrtSBRT + "\t (+/- 3mm)";
            }
            else
            {
                isoCheckResults = CheckResults.Found;
                measuredIsoCoord = " Lat: " + isoLatSBRT + "\t Vrt: " + isoVrtSBRT + "\t Lng: " + isoLongSRS + "\t (+/- 3mm)";
            }


            // Exists 12 different cases that needs to be covered.

            string userOrigoAssumedCorrect = "(calculated from user origin in paranthesis, assuming user origo is correctly positioned in Lat: 300 and Vrt: 95):";
            string origoFound = "(calculated directly from set user origin in paranthesis):";
            string isoNotFound = "Cannot find the SBF in isocenter plane, no automatic check of isocenter possible. ";
            string isoLongNotFound = "Cannot find the isocenter long coordinate in the SBF with profiles. Estimated position of isocenter in SBF coordinates in transverse direction from image profiles ";
            string isoFound = "Estimated position of isocenter in SBF coordinates from image profiles ";
            string warningDiscrepancy = "\n\n * WARNING: discrepancy found between coordinates measured and calculated from user origin";

            switch (origoCheckResults)
            {
                case CheckResults.Found:

                    string calcIsoCoord = "(Lat: " + isoLatSBRTFromUO + ")\t(Vrt: " + isoVrtSBRTFromUO + ")\t(Lng: " + isoLngSBRTFromUO + ")";

                    switch (isoCheckResults)
                    {
                        case CheckResults.Found:
                            // In the case that user origo and isocenter are positioned in the same plane (z), choose not to present the value calculated from user origo long
                            // as this is identical to isocenter long and the same method is used to get it (otherwise might get a false sense that a double check is made). 
                            if (Math.Round(iso.z) == Math.Round(image.UserOrigin.z))
                            {
                                calcIsoCoord = "(Lat: " + isoLatSBRTFromUO + ")\t(Vrt: " + isoVrtSBRTFromUO + ")";
                            }

                            isoSBRTresults = isoFound + origoFound + "\n\n" + measuredIsoCoord + "\n" + calcIsoCoord;
                            if (!CheckCoordAgreement(isoLatSBRT, isoLatSBRTFromUO, isoVrtSBRT, isoVrtSBRTFromUO, isoLongSRS, isoLngSBRTFromUO))
                            {
                                isoSBRTresults += warningDiscrepancy;
                            }
                            break;
                        case CheckResults.NoLong:

                            isoSBRTresults = isoLongNotFound + origoFound + "\n\n" + measuredIsoCoord + "\n" + calcIsoCoord;
                            if (!CheckCoordAgreement(isoLatSBRT, isoLatSBRTFromUO, isoVrtSBRT, isoVrtSBRTFromUO))
                            {
                                isoSBRTresults += warningDiscrepancy;
                            }
                            break;
                        case CheckResults.NotFound:

                            isoSBRTresults = isoNotFound + userOrigoAssumedCorrect + "\n\n" + calcIsoCoord;
                            break;

                        default:
                            break;
                    }


                    break;
                case CheckResults.NoLong:

                    string calcIsoCoordNoLong = "(Lat: " + isoLatSBRTFromUO + ")\t(Vrt: " + isoVrtSBRTFromUO + ")";

                    switch (isoCheckResults)
                    {
                        case CheckResults.Found:

                            isoSBRTresults = isoFound + origoFound + "\n\n" + measuredIsoCoord + "\n" + calcIsoCoordNoLong;
                            if (!CheckCoordAgreement(isoLatSBRT, isoLatSBRTFromUO, isoVrtSBRT, isoVrtSBRTFromUO))
                            {
                                isoSBRTresults += warningDiscrepancy;
                            }
                            break;
                        case CheckResults.NoLong:

                            isoSBRTresults = isoLongNotFound + origoFound + "\n\n" + measuredIsoCoord + "\n" + calcIsoCoordNoLong;
                            if (!CheckCoordAgreement(isoLatSBRT, isoLatSBRTFromUO, isoVrtSBRT, isoVrtSBRTFromUO))
                            {
                                isoSBRTresults += warningDiscrepancy;
                            }
                            break;
                        case CheckResults.NotFound:

                            isoSBRTresults = isoNotFound + userOrigoAssumedCorrect + "\n\n" + calcIsoCoordNoLong;
                            break;

                        default:
                            break;
                    }

                    break;
                case CheckResults.NotFound:

                    switch (isoCheckResults)
                    {
                        case CheckResults.Found:

                            isoSBRTresults = isoFound + userOrigoAssumedCorrect + "\n\n" + measuredIsoCoord + "\n (Lat: " + isoLatSBRTFromUO + ")\t (Vrt: " + isoVrtSBRTFromUO + ")";
                            if (!CheckCoordAgreement(isoLatSBRT, isoLatSBRTFromUO, isoVrtSBRT, isoVrtSBRTFromUO))
                            {
                                isoSBRTresults += warningDiscrepancy;
                            }
                            break;
                        case CheckResults.NoLong:

                            isoSBRTresults = isoLongNotFound + userOrigoAssumedCorrect + "\n\n" + measuredIsoCoord + "\n (Lat: " + isoLatSBRTFromUO + ")\t (Vrt: " + isoVrtSBRTFromUO + ")";
                            if (!CheckCoordAgreement(isoLatSBRT, isoLatSBRTFromUO, isoVrtSBRT, isoVrtSBRTFromUO))
                            {
                                isoSBRTresults += warningDiscrepancy;
                            }
                            break;
                        case CheckResults.NotFound:

                            isoSBRTresults = isoNotFound + userOrigoAssumedCorrect + "\n\n" + "(Lat: " + isoLatSBRTFromUO + ")\t (Vrt: " + isoVrtSBRTFromUO + ")";
                            break;

                        default:
                            break;
                    }
                    break;

                case CheckResults.NotOK:

                    switch (isoCheckResults)
                    {
                        case CheckResults.Found:

                            isoSBRTresults = isoFound + "\n\n" + measuredIsoCoord;

                            break;
                        case CheckResults.NoLong:

                            isoSBRTresults = isoLongNotFound + "\n\n" + measuredIsoCoord;

                            break;
                        case CheckResults.NotFound:

                            isoSBRTresults = isoNotFound;
                            break;

                        default:
                            break;
                    }

                    break;
                default:
                    break;
            }
            return isoSBRTresults;
        }


        private bool CheckCoordAgreement(int lat1, int lat2, int vrt1, int vrt2)
        {
            int tolerance = 3;
            bool lat = Math.Abs(lat1 - lat2) < tolerance;
            bool vrt = Math.Abs(vrt1 - vrt2) < tolerance;
            return lat && vrt;
        }

        private bool CheckCoordAgreement(int lat1, int lat2, int vrt1, int vrt2, int lng1, int lng2)
        {
            int tolerance = 3;
            bool lat = Math.Abs(lat1 - lat2) < tolerance;
            bool vrt = Math.Abs(vrt1 - vrt2) < tolerance;
            bool lng = Math.Abs(lng1 - lng2) < tolerance;
            return lat && vrt && lng;
        }


        // get the lat and long coordinates for the Stereotactic Body Frame setup marker used to position the patient in the SBF
        private string GetSBFSetupCoord(StructureSet ss, CheckResults origoCheckResults, int origoLong)
        {
            string setupSBFResults = "";
            string searchForStructure = "z_SBF_setup"; // there can be only one with the unique ID, Eclipse is also case sensitive

            Structure structSBFMarker = ss.Structures.Where(s => s.Id.ToUpper() == searchForStructure.ToUpper()).SingleOrDefault();

            if (structSBFMarker == null)
            {
                setupSBFResults = "* No SBF marker or structure with ID '" + searchForStructure + "' found. \n";
            }
            else
            {
                var image = ss.Image;
                double bottom;
                double left;
                double right;

                var coord = GetTransverseCoordInSBRTFrame(image, structSBFMarker.CenterPoint);
                left = coord[0];
                right = coord[1];
                bottom = coord[2];
                double lateralCenterSBRT = (left + right) / 2;

                double coordSRSLong = GetSRSLongCoord(image, structSBFMarker.CenterPoint, (int)Math.Round(bottom), left, right);

                int setupSBFLat = Convert.ToInt32(Math.Round(lateralCenterSBRT + 300 - structSBFMarker.CenterPoint.x));     // TODO: this works for HFS and FFS. HFP and FFP unhandled
                int setupSBFLng = (int)Math.Round(coordSRSLong);
                var setupCheckResults = CheckResults.NotFound;
                string measuredSetupCoord = "";


                if (bottom == 0)    // bottom set to 0 if bottom OR lat not found
                {
                    setupCheckResults = CheckResults.NotFound;
                }
                else if (setupSBFLng == 0)
                {
                    setupCheckResults = CheckResults.NoLong;
                    measuredSetupCoord = " Lat: " + setupSBFLat + "\t (+/- 3mm)";
                }
                else
                {
                    setupCheckResults = CheckResults.Found;
                    measuredSetupCoord = " Lat: " + setupSBFLat + "\t Lng: " + setupSBFLng + "\t (+/- 3mm)";
                }

                // Get Lat calculated directly from user origo for comparison
                int setupLatFromUO = (int)(Math.Round(-(structSBFMarker.CenterPoint.x - image.UserOrigin.x - 300)));         // TODO: this works for HFS and FFS. HFP and FFP unhandled
                int setupLngFromUO = (int)(Math.Round(Math.Abs(structSBFMarker.CenterPoint.z - image.UserOrigin.z + origoLong))); // TODO: this works for HFS and FFS. HFP and FFP unhandled



                // Exists 12 different cases that needs to be covered.

                string userOrigoAssumedCorrect = "(calculated from user origin in paranthesis, assuming user origo is correctly positioned in Lat: 300 and Vrt: 95):";
                string origoFound = "(calculated directly from set user origin in paranthesis):";
                string setupNotFound = "Cannot find the SBF in setup marker plane, no automatic check of setup marker position possible. ";
                string setupLongNotFound = "Cannot find the setup marker long coordinate in the SBF with profiles. Estimated position of setup marker in SBF coordinates in transverse direction from image profiles ";
                string setupFound = "Estimated position of the setup marker in SBF coordinates from image profiles ";
                string warningDiscrepancy = "\n\n * WARNING: discrepancy found between coordinates measured and calculated from user origin";

                switch (origoCheckResults)
                {
                    case CheckResults.Found:

                        string calcSetupCoord = "(Lat: " + setupLatFromUO + ")\t(Lng: " + setupLngFromUO + ")";

                        switch (setupCheckResults)
                        {
                            case CheckResults.Found:
                                // In the case that user origo and setup marker are positioned in the same plane (z), choose not to present the value calculated from user origo long
                                // as this is identical to setup marker long and the same method is used to get it (otherwise might get a false sense that a double check is made). 
                                if (Math.Round(structSBFMarker.CenterPoint.z) == Math.Round(image.UserOrigin.z))
                                {
                                    calcSetupCoord = "(Lat: " + setupLatFromUO + ")";
                                }

                                setupSBFResults = setupFound + origoFound + "\n\n" + measuredSetupCoord + "\n" + calcSetupCoord;
                                if (!CheckCoordAgreement(setupSBFLat, setupLatFromUO, setupSBFLng, setupLngFromUO))
                                {
                                    setupSBFResults += warningDiscrepancy;
                                }
                                break;
                            case CheckResults.NoLong:

                                setupSBFResults = setupLongNotFound + origoFound + "\n\n" + measuredSetupCoord + "\n" + calcSetupCoord;
                                if (!CheckCoordAgreement(setupSBFLat, setupLatFromUO, 0, 0))
                                {
                                    setupSBFResults += warningDiscrepancy;
                                }
                                break;
                            case CheckResults.NotFound:

                                setupSBFResults = setupNotFound + userOrigoAssumedCorrect + "\n\n" + calcSetupCoord;
                                break;

                            default:
                                break;
                        }


                        break;
                    case CheckResults.NoLong:

                        string calcIsoCoordNoLong = "(Lat: " + setupLatFromUO + ")";

                        switch (setupCheckResults)
                        {
                            case CheckResults.Found:

                                setupSBFResults = setupFound + origoFound + "\n\n" + measuredSetupCoord + "\n" + calcIsoCoordNoLong;
                                if (!CheckCoordAgreement(setupSBFLat, setupLatFromUO, 0, 0))
                                {
                                    setupSBFResults += warningDiscrepancy;
                                }
                                break;
                            case CheckResults.NoLong:

                                setupSBFResults = setupLongNotFound + origoFound + "\n\n" + measuredSetupCoord + "\n" + calcIsoCoordNoLong;
                                if (!CheckCoordAgreement(setupSBFLat, setupLatFromUO, 0, 0))
                                {
                                    setupSBFResults += warningDiscrepancy;
                                }
                                break;
                            case CheckResults.NotFound:

                                setupSBFResults = setupNotFound + userOrigoAssumedCorrect + "\n\n" + calcIsoCoordNoLong;
                                break;

                            default:
                                break;
                        }

                        break;
                    case CheckResults.NotFound:

                        switch (setupCheckResults)
                        {
                            case CheckResults.Found:

                                setupSBFResults = setupFound + userOrigoAssumedCorrect + "\n\n" + measuredSetupCoord + "\n (Lat: " + setupLatFromUO + ")";
                                if (!CheckCoordAgreement(setupSBFLat, setupLatFromUO, 0, 0))
                                {
                                    setupSBFResults += warningDiscrepancy;
                                }
                                break;
                            case CheckResults.NoLong:

                                setupSBFResults = setupLongNotFound + userOrigoAssumedCorrect + "\n\n" + measuredSetupCoord + "\n (Lat: " + setupLatFromUO + ")";
                                if (!CheckCoordAgreement(setupSBFLat, setupLatFromUO, 0, 0))
                                {
                                    setupSBFResults += warningDiscrepancy;
                                }
                                break;
                            case CheckResults.NotFound:

                                setupSBFResults = setupNotFound + userOrigoAssumedCorrect + "\n\n" + "(Lat: " + setupLatFromUO  + ")";
                                break;

                            default:
                                break;
                        }
                        break;

                    case CheckResults.NotOK:

                        switch (setupCheckResults)
                        {
                            case CheckResults.Found:

                                setupSBFResults = setupFound + "\n\n" + measuredSetupCoord;

                                break;
                            case CheckResults.NoLong:

                                setupSBFResults = setupLongNotFound + "\n\n" + measuredSetupCoord;

                                break;
                            case CheckResults.NotFound:

                                setupSBFResults = setupNotFound;
                                break;

                            default:
                                break;
                        }

                        break;
                    default:
                        break;
                }

                setupSBFResults += "\n\nNote that the long coordinate is the position of the marker (tattoo) and not the long coordinate for positioning the 'coordinate bar' on the SBF.";

            }

            return setupSBFResults;
        }



        /// <summary>
        /// Gets frame of reference in the SBRT frame by returning the dicom coordinate of the SBRT frame bottom (represents 0 in frame coordinates)
        /// and left and right side of the frame in lateral direction, in the plane given by "dicomPosition"
        /// </summary>
        /// <param name="plan"></param>
        /// <param name="dicomPosition"></param>
        /// <returns></returns>
        public double[] GetTransverseCoordInSBRTFrame(Image image, VVector dicomPosition)
        {
            VVector frameOfRefSBRT = dicomPosition;
            int expectedWidth = 442;
            int widthTolerance = 2;

            // get the dicom-position representing vertical coordinate 0 in the SBRT frame
            frameOfRefSBRT.y = GetSBRTBottomCoord(image, dicomPosition);    // igores the position of dicomPosition in x and y and takes only z-position, takes bottom and center of image

            VVector frameOfRefSBRTLeft = frameOfRefSBRT;                    // TODO: left and right designation depending of HFS FFS etc...
            VVector frameOfRefSBRTRight = frameOfRefSBRT;
            double[] returnCoo = new double[3];


            // If bottom found, call method to dubblecheck that the frameOfRefSBRT.y really is the bottom of the SBRT-frame by taking profiles in the sloping part of the SBRT frame and comparing
            // width with expected width at respective height above the bottom
            if (frameOfRefSBRT.y != 0 && DoubleCheckSBRTVrt(image, frameOfRefSBRT))
            {
                double[] coordSRSLat = new double[2];
                coordSRSLat = GetSBRTLatCoord(image, dicomPosition, (int)Math.Round(frameOfRefSBRT.y));
                frameOfRefSBRTLeft.x = coordSRSLat[0];
                frameOfRefSBRTRight.x = coordSRSLat[1];
                frameOfRefSBRT.x = (frameOfRefSBRTLeft.x + frameOfRefSBRTRight.x) / 2;  // middle of the SBRT frame in Lat
                                                                                        // the chances that the dicom coord in x actually is 0.0 is probably small but need to handle this by setting vrt to 0 if no sides found, TODO: could perhaps use nullable ref types 
                                                                                        // Double check that the found width of the SBRT frame is as expected, allow for some flex of the frame and uncertainty of measurement. 
                if (frameOfRefSBRTLeft.x == 0 || frameOfRefSBRTRight.x == 0 || Math.Abs(frameOfRefSBRTLeft.x - frameOfRefSBRTRight.x) < expectedWidth - widthTolerance || Math.Abs(frameOfRefSBRTLeft.x - frameOfRefSBRTRight.x) > expectedWidth + widthTolerance)
                {
                    frameOfRefSBRT.x = 0;
                    frameOfRefSBRT.y = 0;
                }
            }
            else
            {
                frameOfRefSBRT.y = 0; // if double check of vrt failes or bottom not found
            }

            returnCoo[0] = frameOfRefSBRTLeft.x;
            returnCoo[1] = frameOfRefSBRTRight.x;
            returnCoo[2] = frameOfRefSBRT.y;
            return returnCoo;

            // TODO check if isocenter in same plane as user origo, not neccesary though as there can be multiple isocenters (muliple plans) and there is no strict rule...
        }

        private bool DoubleCheckSBRTVrt(Image image, VVector frameOfRefSBRT)
        {
            // 10 mm above the bottom the expected width of the frame is 349 mm (third gradient, i.e. the inner surface of the inner wall) Changes fast with height...
            // approx. 2.4 mm in width per mm in height ( 2*Tan(50) ) , i.e. ca +/-5 mm for +/-2 mm uncertainty in vrt
            bool checkResult = false;
            int expectedWidth = 349;
            int widthTolerans = 5;
            double xLeftUpperCorner = image.Origin.x - image.XRes / 2;  // Dicomcoord in upper left corner ( NOT middle of voxel in upper left corner)
            VVector leftProfileStart = frameOfRefSBRT;                // only to get the z-coord of the passed in VVector, x and y coord will be reassigned
            VVector rightProfileStart = frameOfRefSBRT;               // only to get the z-coord of the passed in VVector, x and y coord will be reassigned
            leftProfileStart.x = xLeftUpperCorner + image.XRes;         // start 1 pixel in left side
            rightProfileStart.x = xLeftUpperCorner + image.XSize * image.XRes - image.XRes;         // start 1 pixel in right side
            leftProfileStart.y = frameOfRefSBRT.y - 10;                 // 10 mm from assumed bottom    
            rightProfileStart.y = leftProfileStart.y;
            double stepsX = image.XRes;             //   (mm/voxel) to make the steps 1 pixel wide, can skip this if 1 mm steps is wanted

            VVector leftProfileEnd = leftProfileStart;
            VVector rightProfileEnd = rightProfileStart;
            leftProfileEnd.x += 200 * stepsX;
            rightProfileEnd.x -= 200 * stepsX;

            var samplesX = (int)Math.Ceiling((leftProfileStart - leftProfileEnd).Length / stepsX);

            var profLeft = image.GetImageProfile(leftProfileStart, leftProfileEnd, new double[samplesX]);
            var profRight = image.GetImageProfile(rightProfileStart, rightProfileEnd, new double[samplesX]);

            List<double> valHULeft = new List<double>();
            List<double> cooLeft = new List<double>();
            for (int i = 0; i < samplesX; i++)
            {
                valHULeft.Add(profLeft[i].Value);
                cooLeft.Add(profLeft[i].Position.x);
            }

            List<double> valHURight = new List<double>();
            List<double> cooRight = new List<double>();
            for (int i = 0; i < samplesX; i++)
            {
                valHURight.Add(profRight[i].Value);
                cooRight.Add(profRight[i].Position.x);
            }


            //***********  Gradient patter describing expected profile in HU of the Lax-box slanted side, from outside to inside **********

            PatternGradient sbrtSide = new PatternGradient();
            sbrtSide.DistanceInMm = new List<double>() { 0, 2, 20 };
            sbrtSide.GradientHUPerMm = new List<int>() { 100, -100, 100 };
            sbrtSide.PositionToleranceMm = new List<int>() { 0, 3, 3 };                        // tolerance for the gradient position
            sbrtSide.GradIndexForCoord = 2;                      // index of gradient position to return (zero based index), i.e. the start of the inner wall

            double[] coordBoxLat = new double[2];
            coordBoxLat[0] = GetGradientCoordinates(cooLeft, valHULeft, sbrtSide.GradientHUPerMm, sbrtSide.DistanceInMm, sbrtSide.PositionToleranceMm, sbrtSide.GradIndexForCoord);
            coordBoxLat[1] = GetGradientCoordinates(cooRight, valHURight, sbrtSide.GradientHUPerMm, sbrtSide.DistanceInMm, sbrtSide.PositionToleranceMm, sbrtSide.GradIndexForCoord);
            //coordBoxLat[2] = ((coordBoxRight + coordBoxLeft) / 2);
            if (coordBoxLat[0] != 0 && coordBoxLat[1] != 0 && Math.Abs(coordBoxLat[1] - coordBoxLat[0]) < expectedWidth + widthTolerans && Math.Abs(coordBoxLat[1] - coordBoxLat[0]) > expectedWidth - widthTolerans)
            {
                checkResult = true;
            }
            //MessageBox.Show("Width of slanted box side " + Math.Abs(coordBoxLat[1] - coordBoxLat[0]).ToString("0.0"));
            return checkResult;
        }


        /// <summary>
        /// Gets the coordinates of the bottom of the SBRT frame, given the plan and the position of interest
        /// Takes the position in center of image in z-coord given by "dicomPosition"
        /// </summary>
        /// <param name="plan"></param>
        /// <param name="dicomPosition"></param>
        /// <returns></returns>
        private int GetSBRTBottomCoord(Image image, VVector dicomPosition)
        {

            double imageSizeX = image.XRes * image.XSize;
            double imageSizeY = image.YRes * image.YSize;
            double xLeftUpperCorner = image.Origin.x - image.XRes / 2;  // Dicomcoord in upper left corner ( NOT middle of voxel in upper left corner)
            double yLeftUpperCorner = image.Origin.y - image.YRes / 2;  // Dicomcoord in upper left corner ( NOT middle of voxel in upper left corner)

            VVector bottomProfileStart = dicomPosition;                      // only to get the z-coord of the user origo, x and y coord will be reassigned
            bottomProfileStart.x = xLeftUpperCorner + imageSizeX / 2;           // center of the image in x-direction
            bottomProfileStart.y = yLeftUpperCorner + imageSizeY - image.YRes;  // start 1 pixel in from bottom...
            double steps = image.YRes;                        //  (mm/voxel) to make the steps 1 pixel wide, can skip this if 1 mm steps is wanted

            VVector bottomProfileEnd = bottomProfileStart;
            bottomProfileEnd.y -= 200 * steps;                                  // endpoint 200 steps in -y direction, i.e. 20 cm if 1 mm pixels

            var samplesY = (int)Math.Ceiling((bottomProfileStart - bottomProfileEnd).Length / steps);


            //***********  Gradient patter describing expected profile in HU of the sbrt-box bottom **********

            PatternGradient sbrt = new PatternGradient();
            sbrt.DistanceInMm = new List<double>() { 0, 4.4, 12.3, 2 };        // distance between gradients, mean values from profiling 10 pat
            sbrt.GradientHUPerMm = new List<int>() { 80, -80, 80, -80 };      // ,  inner shell can be separated from the box, larger tolerance
            sbrt.PositionToleranceMm = new List<int>() { 0, 2, 1, 3 };        // tolerance for the gradient position, needs to be tight as some ct couch tops have almost the same dimensions
            sbrt.GradIndexForCoord = 2;                                     // index of gradient position to return (zero based index)
            double coordBoxBottom = 0;
            int tries = 0;
            List<double> valHU = new List<double>();
            List<double> coo = new List<double>();

            // Per default, try to find bottom in center, in approximately 1 of 20 cases this failes due to couch top structures or image quality, try each side of center
            while (coordBoxBottom == 0 && tries < 3)
            {

                var profY = image.GetImageProfile(bottomProfileStart, bottomProfileEnd, new double[samplesY]);
                // Imageprofile gets a VVector back, take the coordinates and respective HU and put them in two Lists of double, might be better ways of doing this...
                tries++;

                for (int i = 0; i < samplesY; i++)
                {
                    valHU.Add(profY[i].Value);
                    coo.Add(profY[i].Position.y);
                }

                // Get the coordinate (dicom) that represents inner bottom of SBRT frame 
                coordBoxBottom = GetGradientCoordinates(coo, valHU, sbrt.GradientHUPerMm, sbrt.DistanceInMm, sbrt.PositionToleranceMm, sbrt.GradIndexForCoord);
                // in the SBRT frame; VRT 0, which we are looking for, is approximately 1 mm above this gradient position, add 1 mm before returning
                if (coordBoxBottom != 0)
                {
                    coordBoxBottom -= 1;        // TODO: this works for HFS and FFS, HFP and FFP should be handled
                    break;
                }
                else  // if bottom not found at center of image try first 100 mm left, then right
                {
                    valHU.Clear();
                    coo.Clear();
                    if (tries == 1)
                    {
                        bottomProfileStart.x -= 100;
                        bottomProfileEnd.x -= 100;
                    }
                    else
                    {
                        bottomProfileStart.x += 200;
                        bottomProfileEnd.x += 200;
                    }
                }
            }
            return (int)Math.Round(coordBoxBottom);
        }

        private double[,] VoiImageProfile(Image image, VVector start, VVector stop, int resolution, char dir)
        {
            int iX = Math.Abs((int)Math.Round((stop.x - start.x) / resolution));
            int iY = Math.Abs((int)Math.Round((stop.y - start.y) / resolution));
            int iZ = Math.Abs((int)Math.Round((stop.z - start.z) / resolution));
            int widthSamples = 0;
            int widthDir = 0;
            int heightSamples = 0;
            int heightDir = 0;
            int profileSamples = 0;
            int xDir = (int)(Math.Round(stop.x - start.x) / Math.Abs(Math.Round(stop.x - start.x)));
            int yDir = (int)(Math.Round(stop.y - start.y) / Math.Abs(Math.Round(stop.y - start.y)));
            int zDir = (int)(Math.Round(stop.z - start.z) / Math.Abs(Math.Round(stop.z - start.z)));

            switch (dir)
            {
                case 'x':
                    widthSamples = iY;
                    widthDir = yDir;
                    heightSamples = iZ;
                    heightDir = zDir;
                    profileSamples = iX;
                    stop.y = start.y;
                    stop.z = start.z;
                    break;
                case 'y':
                    widthSamples = iX;
                    widthDir = xDir;
                    heightSamples = iZ;
                    heightDir = zDir;
                    profileSamples = iY;
                    stop.x = start.x;
                    stop.z = start.z;
                    break;
                case 'z':
                    widthSamples = iX;
                    widthDir = xDir;
                    heightSamples = iY;
                    heightDir = yDir;
                    profileSamples = iZ;
                    stop.x = start.x;
                    stop.y = start.y;
                    break;
                default:
                    break;
            }
            double[,] values = new double[profileSamples, profileSamples];

            for (int h = 0; h < heightSamples; h++)
            {

                for (int w = 0; w < widthSamples; w++)
                {

                    ImageProfile iProfile = image.GetImageProfile(start, stop, new double[profileSamples]);

                    for (int i = 0; i < profileSamples; i++)
                    {
                        values[1, i] += iProfile[i].Value / (widthSamples * heightSamples);
                    }

                    if (dir.Equals('y') || dir.Equals('z'))
                    {
                        start.x += resolution * widthDir;
                        stop.x += resolution * widthDir;
                    }
                    else
                    {
                        start.y += resolution * widthDir;
                        stop.y += resolution * widthDir;
                    }
                }

                if (dir.Equals('x') || dir.Equals('y'))
                {
                    start.z += resolution * heightDir;
                    stop.z += resolution * heightDir;
                }
                else
                {
                    start.y += resolution * heightDir;
                    stop.y += resolution * heightDir;
                }
            }
            ImageProfile Profile = image.GetImageProfile(start, stop, new double[profileSamples]);

            for (int i = 0; i < profileSamples; i++)
            {
                switch (dir)
                {
                    case 'x':
                        values[0, i] += Profile[i].Position.x;
                        break;
                    case 'y':
                        values[0, i] += Profile[i].Position.y;
                        break;
                    case 'z':
                        values[0, i] += Profile[i].Position.z;
                        break;
                }
            }
            return values;
        }

        // Get profiles in x direction, left and right side and determine center of box in the plane determined by dicomCoord
        private double[] GetSBRTLatCoord(Image image, VVector dicomCoord, int coordSBFBottom)
        {
            double xLeftUpperCorner = image.Origin.x - image.XRes / 2;  // Dicomcoord in upper left corner ( NOT middle of voxel in upper left corner) check for FFS, FFP, HFP
            VVector leftProfileStart = dicomCoord;                // only to get the z-coord of the passed in VVector, x and y coord will be reassigned
            VVector rightProfileStart = dicomCoord;               // only to get the z-coord of the passed in VVector, x and y coord will be reassigned
            leftProfileStart.x = xLeftUpperCorner + image.XRes;         // start 1 pixel in left side
            rightProfileStart.x = xLeftUpperCorner + image.XSize * image.XRes - image.XRes;         // start 1 pixel in right side
            leftProfileStart.y = coordSBFBottom - 91.5;                 // between index fidusle (Vrt 95) and lower fidusles      
            rightProfileStart.y = leftProfileStart.y;
            double stepsX = image.XRes;             //   (mm/voxel) to make the steps 1 pixel wide, can skip this if 1 mm steps is wanted

            VVector leftProfileEnd = leftProfileStart;
            VVector rightProfileEnd = rightProfileStart;
            leftProfileEnd.x += 100 * stepsX;                   // endpoint 100 steps in  direction
            rightProfileEnd.x -= 100 * stepsX;

            var samplesX = (int)Math.Ceiling((leftProfileStart - leftProfileEnd).Length / stepsX);

            var profLeft = image.GetImageProfile(leftProfileStart, leftProfileEnd, new double[samplesX]);
            var profRight = image.GetImageProfile(rightProfileStart, rightProfileEnd, new double[samplesX]);

            List<double> valHULeft = new List<double>();
            List<double> cooLeft = new List<double>();
            string debugLeft = "";
            for (int i = 0; i < samplesX; i++)
            {
                valHULeft.Add(profLeft[i].Value);
                cooLeft.Add(profLeft[i].Position.x);
                if (i > 0)
                {
                    debugLeft += cooLeft[i].ToString("0.0") + "\t" + (valHULeft[i] - valHULeft[i - 1]).ToString("0.0") + "\n";
                }
            }

            List<double> valHURight = new List<double>();
            List<double> cooRight = new List<double>();

            for (int i = 0; i < samplesX; i++)
            {
                valHURight.Add(profRight[i].Value);
                cooRight.Add(profRight[i].Position.x);
            }

            //Gradient patter describing expected profile in HU of the SBF side, from outside to inside

            PatternGradient sbrtSide = new PatternGradient();
            sbrtSide.DistanceInMm = new List<double>() { 0, 2, 13 };     // distance between gradients, mean values from profiling 10 pat 
            sbrtSide.GradientHUPerMm = new List<int>() { 100, -100, 100 };
            sbrtSide.PositionToleranceMm = new List<int>() { 0, 2, 2 };                        // tolerance for the gradient position
            sbrtSide.GradIndexForCoord = 2;                      // index of gradient position to return (zero based index), i.e. the start of the inner wall

            double[] coordBoxLat = new double[2];
            coordBoxLat[0] = GetGradientCoordinates(cooLeft, valHULeft, sbrtSide.GradientHUPerMm, sbrtSide.DistanceInMm, sbrtSide.PositionToleranceMm, sbrtSide.GradIndexForCoord);
            coordBoxLat[1] = GetGradientCoordinates(cooRight, valHURight, sbrtSide.GradientHUPerMm, sbrtSide.DistanceInMm, sbrtSide.PositionToleranceMm, sbrtSide.GradIndexForCoord);

            return coordBoxLat;
        }

        private double GetSRSLongCoord(Image image, VVector dicomPosition, int coordSRSBottom, double coordBoxLeft, double coordBoxRight)
        {
            string debug = "";

            // Start with lower profiles, i.e. to count the number of fidusles determining the long position in decimeter
            // Assuming the bottom part of the wall doesn't flex and there are no roll

            double offsetSides = 2;
            double offsetBottom = 91.5;
            int searchRange = 8;

            VVector leftFidusLowerStart = dicomPosition;                // only to get the z-coord of the dicomPosition, x and y coord will be reassigned
            VVector rightFidusLowerStart = dicomPosition;               // only to get the z-coord of the dicomPosition, x and y coord will be reassigned
            leftFidusLowerStart.x = coordBoxLeft + offsetSides;                        // start a small distance in from gradient found in previous step
            rightFidusLowerStart.x = coordBoxRight - offsetSides;
            leftFidusLowerStart.y = coordSRSBottom - offsetBottom;                  // hopefully between fidusles...
            rightFidusLowerStart.y = leftFidusLowerStart.y;
            double stepLength = 0.5;                                                //   probably need sub-mm steps to get the fidusle-positions

            VVector leftFidusLowerEnd = leftFidusLowerStart;
            VVector rightFidusLowerEnd = rightFidusLowerStart;

            int lowerProfileDistance = 40;                                  // profile length to include all possible fidusles

            leftFidusLowerEnd.y += lowerProfileDistance;                   // distance containing all fidusles determining the Long in 10 cm steps
            rightFidusLowerEnd.y += lowerProfileDistance;                  // distance containing all fidusles determining the Long in 10 cm steps

            var samplesFidusLower = (int)Math.Ceiling((leftFidusLowerStart - leftFidusLowerEnd).Length / stepLength);

            leftFidusLowerStart.x = GetMaxHUX(image, leftFidusLowerStart, leftFidusLowerEnd, searchRange, samplesFidusLower);
            rightFidusLowerStart.x = GetMaxHUX(image, rightFidusLowerStart, rightFidusLowerEnd, -searchRange, samplesFidusLower);
            leftFidusLowerEnd.x = leftFidusLowerStart.x;
            rightFidusLowerEnd.x = rightFidusLowerStart.x;

            int numberOfFidusLeft = GetNumberOfFidus(image, leftFidusLowerStart, leftFidusLowerEnd, lowerProfileDistance * 2);
            int numberOfFidusRight = GetNumberOfFidus(image, rightFidusLowerStart, rightFidusLowerEnd, lowerProfileDistance * 2);

            // Since the SRS-box walls flexes, the x-coordinate for upper profile may differ from start to end
            // get the max HU in the upper part of the box ( top-most fidusel ) to determine the final x-value for the profile
            // also need to get the x-value for start of the profile, concentrate on index-fidusle (Vrt 95 in SBRT frame coordinates)

            // Start with finding the optimal position in x for the index fidusle, left and right

            VVector leftIndexFidusStart = dicomPosition;
            VVector rightIndexFidusStart = dicomPosition;
            leftIndexFidusStart.x = coordBoxLeft + offsetSides;
            rightIndexFidusStart.x = coordBoxRight - offsetSides;
            leftIndexFidusStart.y = coordSRSBottom - offsetBottom;
            rightIndexFidusStart.y = coordSRSBottom - offsetBottom;
            VVector leftIndexFidusEnd = leftIndexFidusStart;
            VVector rightIndexFidusEnd = rightIndexFidusStart;
            int shortProfileLength = 20;
            leftIndexFidusEnd.y -= shortProfileLength;
            rightIndexFidusEnd.y -= shortProfileLength;

            VVector leftFidusUpperStart = leftIndexFidusStart;      // to get the z-coord, x and y coord will be reassigned
            VVector rightFidusUpperStart = rightIndexFidusStart;

            leftFidusUpperStart.x = GetMaxHUX(image, leftIndexFidusStart, leftIndexFidusEnd, searchRange, shortProfileLength * 2);
            rightFidusUpperStart.x = GetMaxHUX(image, rightIndexFidusStart, rightIndexFidusEnd, -searchRange, shortProfileLength * 2);


            // startposition for profiles determined, next job the endposition

            int upperProfileDistance = 115;
            VVector leftTopFidusStart = dicomPosition;
            VVector rightTopFidusStart = dicomPosition;
            leftTopFidusStart.x = coordBoxLeft + offsetSides;
            rightTopFidusStart.x = coordBoxRight - offsetSides;
            leftTopFidusStart.y = coordSRSBottom - offsetBottom - upperProfileDistance + shortProfileLength;  // unnecessary complex but want to get the profile in same direction
            rightTopFidusStart.y = coordSRSBottom - offsetBottom - upperProfileDistance + shortProfileLength;
            VVector leftTopFidusEnd = leftTopFidusStart;
            VVector rightTopFidusEnd = rightTopFidusStart;

            leftTopFidusEnd.y -= shortProfileLength;
            rightTopFidusEnd.y -= shortProfileLength;

            VVector leftFidusUpperEnd = leftTopFidusEnd;
            VVector rightFidusUpperEnd = rightTopFidusEnd;

            leftFidusUpperEnd.x = GetMaxHUX(image, leftTopFidusStart, leftTopFidusEnd, searchRange, shortProfileLength * 2);                      
            rightFidusUpperEnd.x = GetMaxHUX(image, rightTopFidusStart, rightTopFidusEnd, -searchRange, shortProfileLength * 2);

            debug += "LeftBox: \t\t" + coordBoxLeft.ToString("0.0") + "\n";
            debug += "LeftLow xstart: \t" + leftFidusLowerStart.x.ToString("0.0") + "\n";
            debug += "Left x start: \t" + leftFidusUpperStart.x.ToString("0.0") + "\n End: \t\t" + leftFidusUpperEnd.x.ToString("0.0") + "\n\n";

            debug += "RightBox: \t\t" + coordBoxRight.ToString("0.0") + "\n";
            debug += "RightLow xstart: \t" + rightFidusLowerStart.x.ToString("0.0") + "\n";
            debug += "Right x start: \t" + rightFidusUpperStart.x.ToString("0.0") + "\n End: \t\t" + rightFidusUpperEnd.x.ToString("0.0") + "\n\n\n";

            double fidusLongLeft = GetLongFidus(image, leftFidusUpperStart, leftFidusUpperEnd, upperProfileDistance * 2);
            double fidusLongRight = GetLongFidus(image, rightFidusUpperStart, rightFidusUpperEnd, upperProfileDistance * 2);

            debug += "Left side long:  " + fidusLongLeft.ToString("0.0") + "\t Right side long:  " + fidusLongRight.ToString("0.0") + "\n\n";
            debug += "Left side fidus:  " + numberOfFidusLeft + "\t Right side fidus:  " + numberOfFidusRight;

            //MessageBox.Show(debug);
            // Also need to check the long coordinate above and below (in z-dir) in case its a boundary case where the number of fidusles 
            // steps up. Only neccesary in case of large value for fidusLong or if a discrepancy between the number of fidusles found left and right,
            // or if the long value is not found. +/- 10 mm shift in z-dir is enough to avoid boundary condition 

            double coordSRSLong = 0;

            if (numberOfFidusLeft != numberOfFidusRight || fidusLongLeft > 97 || fidusLongRight > 97 || fidusLongLeft == 0.0 || fidusLongRight == 0)
            {
                int shiftZ = 10;
                leftFidusLowerStart.z += shiftZ;
                leftFidusLowerEnd.z += shiftZ;
                leftFidusUpperEnd.z += shiftZ;
                rightFidusLowerStart.z += shiftZ;
                rightFidusLowerEnd.z += shiftZ;
                rightFidusUpperEnd.z += shiftZ;


                int nOfFidusLeft1 = GetNumberOfFidus(image, leftFidusLowerStart, leftFidusLowerEnd, lowerProfileDistance * 2);
                double fidusLLeft1 = GetLongFidus(image, leftFidusLowerStart, leftFidusUpperEnd, upperProfileDistance * 2);
                int nOfFidusRight1 = GetNumberOfFidus(image, rightFidusLowerStart, rightFidusLowerEnd, lowerProfileDistance * 2);
                double fidusLRight1 = GetLongFidus(image, rightFidusLowerStart, rightFidusUpperEnd, upperProfileDistance * 2);


                leftFidusLowerStart.z -= 2 * shiftZ;
                leftFidusLowerEnd.z -= 2 * shiftZ;
                leftFidusUpperEnd.z -= 2 * shiftZ;
                rightFidusLowerStart.z -= 2 * shiftZ;
                rightFidusLowerEnd.z -= 2 * shiftZ;
                rightFidusUpperEnd.z -= 2 * shiftZ;


                int nOfFidusLeft2 = GetNumberOfFidus(image, leftFidusLowerStart, leftFidusLowerEnd, lowerProfileDistance * 2);
                double fidusLLeft2 = GetLongFidus(image, leftFidusLowerStart, leftFidusUpperEnd, upperProfileDistance * 2);
                int nOfFidusRight2 = GetNumberOfFidus(image, rightFidusLowerStart, rightFidusLowerEnd, lowerProfileDistance * 2);
                double fidusLRight2 = GetLongFidus(image, rightFidusLowerStart, rightFidusUpperEnd, upperProfileDistance * 2);


                double coordLong1 = (nOfFidusLeft1 + nOfFidusRight1) * 50 + (fidusLLeft1 + fidusLRight1) / 2;
                double coordLong2 = (nOfFidusLeft2 + nOfFidusRight2) * 50 + (fidusLLeft2 + fidusLRight2) / 2;

                // Check if resonable agreement before assigning the final long coordinate as mean value, hard coded values for uncertainty...
                // left and right side should be within 2 mm and not zero
                // moved 10 mm in both directions i.e. expected difference in long is 20 mm
                if (nOfFidusLeft1 == nOfFidusRight1 && nOfFidusLeft2 == nOfFidusRight2 && Math.Abs(fidusLLeft1 - fidusLRight1) < 2 && Math.Abs(fidusLLeft2 - fidusLRight2) < 2 && fidusLLeft1 != 0 && fidusLLeft2 != 0)
                {
                    if (Math.Abs(coordLong2 - coordLong1) > 18 && Math.Abs(coordLong2 - coordLong1) < 22)
                    {
                        coordSRSLong = (coordLong1 + coordLong2) / 2;
                    }
                }
                else
                {
                    //	MessageBox.Show("Problem :first " + coordLong1.ToString("0.0") + "\t second " + coordLong2.ToString("0.0"));
                }
            }
            else
            {
                coordSRSLong = (numberOfFidusLeft + numberOfFidusRight) * 50 + (fidusLongLeft + fidusLongRight) / 2;
                //MessageBox.Show("Left side " + fidusLongLeft.ToString("0.0") + "\t Right side " + fidusLongRight.ToString("0.0"));
            }
            return coordSRSLong;
        }




        /// <summary>
        /// gets the x-value where maximum HU is found when stepping the y-profile in the direction (Dicom) and range given in steps of 0.1 mm
        /// </summary>
        /// <param name="image"></param>
        /// <param name="fidusStart"></param>
        /// <param name="fidusEnd"></param>
        /// <param name="dirLengthInmm"></param>
        /// <param name="samples"></param>
        /// <returns></returns>
        public static double GetMaxHUX(Image image, VVector fidusStart, VVector fidusEnd, double dirLengthInmm, int samples)
        {
            double newMax = 0.0;
            string debugM = "";
            List<double> HUTemp = new List<double>();
            List<double> cooTemp = new List<double>();
            double finalXValue = 0;
            for (int s = 0; s < 10 * Math.Abs(dirLengthInmm); s++)
            {
                fidusStart.x += 0.1 * dirLengthInmm / Math.Abs(dirLengthInmm);  // ugly way to get the direction                     
                fidusEnd.x = fidusStart.x;


                var profFidus = image.GetImageProfile(fidusStart, fidusEnd, new double[samples]);

                for (int i = 0; i < samples; i++)
                {
                    HUTemp.Add(profFidus[i].Value);
                    cooTemp.Add(profFidus[i].Position.y);
                }
                if (HUTemp.Max() > newMax)
                {
                    newMax = HUTemp.Max();
                    finalXValue = fidusStart.x;
                    debugM += finalXValue.ToString("0.0") + "\t" + newMax.ToString("0.0") + "\n";
                }
                HUTemp.Clear();
                cooTemp.Clear();
            }
            //MessageBox.Show(debugM);
            return finalXValue;
        }


        public int GetNumberOfFidus(Image image, VVector fidusStart, VVector fidusEnd, int samples)
        {
            List<double> valHU = new List<double>();
            List<double> coord = new List<double>();
            double findGradientResult;

            var profFidus = image.GetImageProfile(fidusStart, fidusEnd, new double[samples]);

            for (int i = 0; i < samples; i++)
            {
                valHU.Add(profFidus[i].Value);
                coord.Add(profFidus[i].Position.y);
            }
            var fid = new PatternGradient();
            fid.DistanceInMm = new List<double>() { 0, 2 };         // distance between gradients
            fid.GradientHUPerMm = new List<int>() { 100, -100 };    // smallest number of fidusles is one?  actually its zero! TODO have to handle this case!!
            fid.PositionToleranceMm = new List<int>() { 0, 2 };     // tolerance for the gradient position, parameter to optimize depending probably of resolution of profile
            fid.GradIndexForCoord = 0;                              // index of gradient position to return, in this case used only as a counter for number of fidusles

            findGradientResult = GetGradientCoordinates(coord, valHU, fid.GradientHUPerMm, fid.DistanceInMm, fid.PositionToleranceMm, fid.GradIndexForCoord);
            // keep adding gradient pattern until no more fidusles found
            while (findGradientResult != 0.0)
            {
                fid.DistanceInMm.Add(3);
                fid.GradientHUPerMm.Add(100);
                fid.PositionToleranceMm.Add(2);
                fid.DistanceInMm.Add(2);
                fid.GradientHUPerMm.Add(-100);
                fid.PositionToleranceMm.Add(2);
                fid.GradIndexForCoord++;
                findGradientResult = GetGradientCoordinates(coord, valHU, fid.GradientHUPerMm, fid.DistanceInMm, fid.PositionToleranceMm, fid.GradIndexForCoord);
            }
            return fid.GradIndexForCoord;
        }




        public double GetLongFidus(Image image, VVector fidusStart, VVector fidusEnd, int samples)
        {
            List<double> valHU = new List<double>();
            List<double> coord = new List<double>();
            double findFirstFidus;
            double findSecondFidus;


            var profFidus = image.GetImageProfile(fidusStart, fidusEnd, new double[samples]);

            for (int i = 0; i < samples; i++)
            {
                valHU.Add(profFidus[i].Value);
                coord.Add(profFidus[i].Position.y);
            }


            int diagFidusGradient = 100 / (int)Math.Round(Math.Sqrt((Math.Sqrt(image.ZRes))));  //diagonal fidusle have flacker gradient
                                                                                                //double diagFidusGradientMinLength = 2 * Math.Sqrt(image.ZRes);
            double diagFidusWidth = 1 + 0.5 * Math.Sqrt(image.ZRes);                        // and is wider, both values depend on resolution in Z


            var fid = new PatternGradient();
            fid.DistanceInMm = new List<double>() { 0, 2, 49, diagFidusWidth, 99, 2 };//};        // distance between gradients
            fid.GradientHUPerMm = new List<int>() { 100, -100, diagFidusGradient, -diagFidusGradient, 100, -100 };//};    // diagonal fidusle have flacker gradient
                                                                                                                  //fid.MinGradientLength = new List<double>() { 0, 0, 0, 0, 0, 0 };//};        // minimum length of gradient, needed in case of noicy image
            fid.PositionToleranceMm = new List<int>() { 2, 3, 105, 4, 105, 3 };                        // tolerance for the gradient position, in this case the maximum distance is approx 105 mm
            fid.GradIndexForCoord = 0;                      // index of gradient position to return (zero based index)

            // Finding position of the gradient start is not enough since the long fidusle is diagonal and also changes width depending of the resolution of the image in z-dir, 
            // have to take the mean position before and after.
            double findFirstFidusStart = GetGradientCoordinates(coord, valHU, fid.GradientHUPerMm, fid.DistanceInMm, fid.PositionToleranceMm, fid.GradIndexForCoord);
            fid.GradIndexForCoord = 1;
            double findFirstFidusEnd = GetGradientCoordinates(coord, valHU, fid.GradientHUPerMm, fid.DistanceInMm, fid.PositionToleranceMm, fid.GradIndexForCoord);
            findFirstFidus = (findFirstFidusStart + findFirstFidusEnd) / 2;
            //Find position of second fidus (diagonal)

            fid.GradIndexForCoord = 2;
            double findSecondFidusStart = GetGradientCoordinates(coord, valHU, fid.GradientHUPerMm, fid.DistanceInMm, fid.PositionToleranceMm, fid.GradIndexForCoord);
            fid.GradIndexForCoord = 3;
            double findSecondFidusEnd = GetGradientCoordinates(coord, valHU, fid.GradientHUPerMm, fid.DistanceInMm, fid.PositionToleranceMm, fid.GradIndexForCoord);
            findSecondFidus = (findSecondFidusStart + findSecondFidusEnd) / 2;

            return Math.Abs(findSecondFidus - findFirstFidus);
        }






        /// <summary>
        /// gives the Dicom-coordinates of a gradient 
        /// </summary>
        /// <param name="coord"> 1D coordinates of profile in mm</param>
        /// <param name="val"> values of the profile</param>
        /// <param name="valPermm"> Gradient to search for in value/mm with sign indicating direction</param>
        /// <param name="dist"> Distance in mm to the next gradient</param>
        /// <param name="posTolerance"> Tolerance of position of found gradient in mm</param>
        /// <returns></returns>
        public double GetGradientCoordinates(List<double> coord, List<double> val, List<int> valPermm, List<double> dist, List<int> posTolerance, int indexToReturn)
        {
            string debug = "";
            double[] grad = new double[coord.Count - 1];
            double[] pos = new double[coord.Count - 1];
            int index = 0;

            double gradientStart;
            double gradientEnd;
            double gradientMiddle;
            // resample profile to gradient with position inbetween profile points ( number of samples decreases with one)
            for (int i = 0; i < coord.Count - 2; i++)
            {
                pos[i] = (coord[i] + coord[i + 1]) / 2;
                grad[i] = (val[i + 1] - val[i]) / Math.Abs(coord[i + 1] - coord[i]);
            }

            List<double> gradPosition = new List<double>();
            int indexToReturnToInCaseOfFail = 0;

            for (int i = 0; i < pos.Count(); i++)
            {
                if (index == valPermm.Count())                        //break if last condition passed 
                {
                    break;
                }
                // if gradient larger than given gradient and in the same direction
                //if (Math.Abs((valueHU[i + 1] - valueHU[i]) / Math.Abs(coord[i + 1] - coord[i])) > (Math.Abs(hUPerMm[index])) && SameSign(grad[i], hUPerMm[index]))
                if (Math.Abs(grad[i]) > Math.Abs(valPermm[index]) && SameSign(grad[i], valPermm[index]))
                {
                    gradientStart = pos[i];
                    gradientEnd = pos[i];

                    //Keep stepping up while gradient larger than given huPerMm
                    while (Math.Abs(grad[i]) > (Math.Abs(valPermm[index])) && SameSign(grad[i], valPermm[index]) && i < coord.Count - 2)
                    {
                        i++;
                        gradientEnd = pos[i];
                        if (index == 0)
                        {
                            indexToReturnToInCaseOfFail = i + 1; // if the search fails, i.e. can not find next gradient within the distance given, return to position directly after first gradient ends
                        }
                    }
                    gradientMiddle = (gradientStart + gradientEnd) / 2;
                    // if this is the first gradient (i.e. index == 0), cannot yet compare the distance between the gradients, step up index and continue
                    if (index == 0)
                    {
                        gradPosition.Add(gradientMiddle);
                        index++;
                    }
                    // if gradient found before expected position (outside tolerance), keep looking
                    else if (Math.Abs(gradientMiddle - gradPosition[index - 1]) < dist[index] - posTolerance[index] && i < pos.Count() - 2)
                    {
                        i++;
                        //MessageBox.Show(Math.Abs(gradientMiddle - gradPosition[index - 1]).ToString("0.0"));
                    }
                    // if next gradient not found within tolerance distance, means that the first gradient is probably wrong, reset index
                    else if ((Math.Abs(gradientMiddle - gradPosition[index - 1]) > (Math.Abs(dist[index]) + posTolerance[index])))
                    {
                        debug += "Fail " + (Math.Abs(gradientMiddle - gradPosition[index - 1])).ToString("0.0") + "\t" + (dist[index] + posTolerance[index]).ToString("0.0") + "\n";
                        gradPosition.Clear();
                        index = 0;
                        i = indexToReturnToInCaseOfFail;
                    }
                    //  compare the distance between the gradients to the criteria given, step up index and continue if within tolerance
                    else if ((Math.Abs(gradientMiddle - gradPosition[index - 1]) > (dist[index] - posTolerance[index])) && (Math.Abs(gradientMiddle - gradPosition[index - 1]) < (dist[index] + posTolerance[index])))
                    {
                        gradPosition.Add(gradientMiddle);
                        index++;
                        if (index == 1)
                        {
                            indexToReturnToInCaseOfFail = i;
                        }
                    }
                    else
                    {   // if not the first gradient and the distance betwen the gradients are not met within the tolerance; reset index and positions and continue search
                        // reset search from second gradient position to avoid missing the actual gradient.
                        if (gradPosition.Count > 1 && indexToReturnToInCaseOfFail > 0)
                        {
                            i = indexToReturnToInCaseOfFail;
                        }
                        gradPosition.Clear();
                        index = 0;
                    }
                }
            }
            if (index == valPermm.Count())
            {
                return gradPosition[indexToReturn];
            }
            else
            {
                return 0.0;
            }
        } // end method 

    }
}
