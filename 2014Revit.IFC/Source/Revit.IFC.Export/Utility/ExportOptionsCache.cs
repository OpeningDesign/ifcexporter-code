﻿//
// BIM IFC library: this library works with Autodesk(R) Revit(R) to export IFC files containing model geometry.
// Copyright (C) 2013  Autodesk, Inc.
// 
// This library is free software; you can redistribute it and/or
// modify it under the terms of the GNU Lesser General Public
// License as published by the Free Software Foundation; either
// version 2.1 of the License, or (at your option) any later version.
//
// This library is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
// Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public
// License along with this library; if not, write to the Free Software
// Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301  USA
//

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;

namespace Revit.IFC.Export.Utility
{
    /// <summary>
    /// The cache which holds all export options.
    /// </summary>
    public class ExportOptionsCache
    {
        private GUIDOptions m_GUIDOptions;

        /// <summary>
        /// Private default constructor.
        /// </summary>
        private ExportOptionsCache()
        { }


        /// <summary>
        /// de-serialize vector passed from UI trough options 
        /// </summary>
        private static XYZ ParseXYZ(string value)
        {
            XYZ retVal = null;

            //split string to components by removing seprator characters
            string[] separator = new string[] { ",", "(", ")", " " };
            string[] sList = new string[3] { "", "", "" };
            sList = value.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            //should remain only 3 values if everything is OK

            try
            {
                double valX = double.Parse(sList[0]); //parsing values
                double valY = double.Parse(sList[1]);
                double valZ = double.Parse(sList[2]);
                //if no exception then put it in return value
                retVal = new XYZ(valX, valY, valZ);
            }
            catch (FormatException)
            {

            }
            //return null if there is a problem or a value 
            return retVal;
        }

        /// <summary>
        /// de-serialize transform passed from UI trough options 
        /// </summary>
        private static Transform ParseTransform(string value)
        {
            Transform retVal = null;

            try
            {
                //spit string by separator; it should remain 4 items
                string[] separator = new string[] { ";" };
                string[] sList = new string[4] { "", "", "", "" };

                sList = value.Split(separator, StringSplitOptions.RemoveEmptyEntries);
                Transform tr = new Transform(Transform.Identity);
                // parse each item in part
                tr.Origin = ParseXYZ(sList[0]);
                tr.BasisX = ParseXYZ(sList[1]);
                tr.BasisY = ParseXYZ(sList[2]);
                tr.BasisZ = ParseXYZ(sList[3]);
                // verify if value was correctly parsed
                if (tr.Origin != null && tr.BasisX != null &&
                    tr.BasisY != null && tr.BasisZ != null)
                    retVal = tr;
            }
            catch
            {
                retVal = null;
            }

            //return value
            return retVal;
        }

        private static ElementId ParseElementId(String singleElementValue)
        {
            int elementIdAsInt;
            if (Int32.TryParse(singleElementValue, out elementIdAsInt))
            {
                return new ElementId(elementIdAsInt);
            }
            else
            {
                // Error - the option supplied could not be mapped to int.
                // TODO: consider logging this error later and handling results better.
                throw new Exception("String did not map to a usable element id");
            }
        }

        private static IList<ElementId> ParseElementIds(String elementsToExportValue)
        {
            String[] elements = elementsToExportValue.Split(';');
            List<ElementId> ids = new List<ElementId>();
            foreach (String element in elements)
            {
                int elementIdAsInt;
                if (Int32.TryParse(element, out elementIdAsInt))
                {
                    ids.Add(new ElementId(elementIdAsInt));
                }
                else
                {
                    // Error - the option supplied could not be mapped to int.
                    // TODO: consider logging this error later and handling results better.
                    throw new Exception("Substring " + element + " did not map to a usable element id");
                }
            }
            return ids;
        }
        
        /// <summary>
        /// Creates a new export options cache from the data in the ExporterIFC passed from Revit.
        /// </summary>
        /// <param name="exporterIFC">The ExporterIFC handle passed during export.</param>
        /// <returns>The new cache.</returns>
        public static ExportOptionsCache Create(ExporterIFC exporterIFC, Document document, Autodesk.Revit.DB.View filterView)
        {
            IDictionary<String, String> options = exporterIFC.GetOptions();

            ExportOptionsCache cache = new ExportOptionsCache();
            cache.FileVersion = exporterIFC.FileVersion;
            cache.FileName = exporterIFC.FileName;
            cache.ExportBaseQuantities = exporterIFC.ExportBaseQuantities;
            cache.WallAndColumnSplitting = exporterIFC.WallAndColumnSplitting;
            cache.SpaceBoundaryLevel = exporterIFC.SpaceBoundaryLevel;
            // Export Part element only if 'Current View Only' is checked and 'Show Parts' is selected. 
            cache.ExportParts = filterView != null && filterView.PartsVisibility == PartsVisibility.ShowPartsOnly;
            cache.ExportPartsAsBuildingElementsOverride = null;
            cache.ExportAllLevels = false;
            cache.ExportAnnotationsOverride = null;
            cache.FilterViewForExport = filterView;
            cache.ExportBoundingBoxOverride = null;
            cache.IncludeSiteElevation = false;
            cache.UseCoarseTessellation = true;

            cache.PropertySetOptions = PropertySetOptions.Create(exporterIFC, cache);

            String use2DRoomBoundary = Environment.GetEnvironmentVariable("Use2DRoomBoundaryForRoomVolumeCalculationOnIFCExport");
            bool? use2DRoomBoundaryOption = GetNamedBooleanOption(options, "Use2DRoomBoundaryForVolume");
            cache.Use2DRoomBoundaryForRoomVolumeCreation =
                ((use2DRoomBoundary != null && use2DRoomBoundary == "1") ||
                cache.ExportAs2x2 ||
                (use2DRoomBoundaryOption != null && use2DRoomBoundaryOption.GetValueOrDefault()));

            bool? exportAdvancedSweptSolids = GetNamedBooleanOption(options, "ExportAdvancedSweptSolids");
            cache.ExportAdvancedSweptSolids = (exportAdvancedSweptSolids.HasValue) ? exportAdvancedSweptSolids.Value : false;

            // Set GUIDOptions here.
            {
                // This option should be rarely used, and is only for consistency with old files.  As such, it is set by environment variable only.
                String use2009GUID = Environment.GetEnvironmentVariable("Assign2009GUIDToBuildingStoriesOnIFCExport");
                cache.GUIDOptions.Use2009BuildingStoreyGUIDs = (use2009GUID != null && use2009GUID == "1");

                bool? allowGUIDParameterOverride = GetNamedBooleanOption(options, "AllowGUIDParameterOverride");
                if (allowGUIDParameterOverride != null)
                    cache.GUIDOptions.AllowGUIDParameterOverride = allowGUIDParameterOverride.Value;

                bool? storeIFCGUID = GetNamedBooleanOption(options, "StoreIFCGUID");
                if (storeIFCGUID != null)
                    cache.GUIDOptions.StoreIFCGUID = storeIFCGUID.Value;
            }

            // Set NamingOptions here.
            cache.NamingOptions = new NamingOptions();
            {
                bool? useFamilyAndTypeNameForReference = GetNamedBooleanOption(options, "UseFamilyAndTypeNameForReference");
                cache.NamingOptions.UseFamilyAndTypeNameForReference =
                    (useFamilyAndTypeNameForReference != null) && useFamilyAndTypeNameForReference.GetValueOrDefault();

                bool? useVisibleRevitNameAsEntityName = GetNamedBooleanOption(options, "UseVisibleRevitNameAsEntityName");
                cache.NamingOptions.UseVisibleRevitNameAsEntityName =
                    (useVisibleRevitNameAsEntityName != null) && useVisibleRevitNameAsEntityName.GetValueOrDefault();
            }

            // "SingleElement" export option - useful for debugging - only one input element will be processed for export
            String singleElementValue;
            String elementsToExportValue;
            if (options.TryGetValue("SingleElement", out singleElementValue))
            {
                ElementId elementId = ParseElementId(singleElementValue);
                
                List<ElementId> ids = new List<ElementId>();
                ids.Add(elementId);
                cache.ElementsForExport = ids;
            }
            else if (options.TryGetValue("ElementsForExport", out elementsToExportValue))
            {
                IList<ElementId> ids = ParseElementIds(elementsToExportValue);
                cache.ElementsForExport = ids;
            }
            else
            {
                cache.ElementsForExport = new List<ElementId>();
            }

            // "ExportAnnotations" override
            cache.ExportAnnotationsOverride = GetNamedBooleanOption(options, "ExportAnnotations");

            // "ExportSeparateParts" override
            cache.ExportPartsAsBuildingElementsOverride = GetNamedBooleanOption(options, "ExportPartsAsBuildingElements");

            // "ExportBoundingBox" override
            cache.ExportBoundingBoxOverride = GetNamedBooleanOption(options, "ExportBoundingBox");

            // Using the alternate UI or not.
            cache.AlternateUIVersionOverride = GetNamedStringOption(options, "AlternateUIVersion");

            // Include IFCSITE elevation in the site local placement origin
            bool? includeIfcSiteElevation = GetNamedBooleanOption(options, "IncludeSiteElevation");
            cache.IncludeSiteElevation = includeIfcSiteElevation != null ? includeIfcSiteElevation.Value : false;

            // Use coarse tessellation for floors, railings, ramps, spaces and stairs.
            bool? useCoarseTessellation = GetNamedBooleanOption(options, "UseCoarseTessellation");
            cache.UseCoarseTessellation = useCoarseTessellation != null ? useCoarseTessellation.Value : true;

            /// Allow exporting a mix of extrusions and BReps as a solid model, if possible.
            bool? canExportSolidModelRep = GetNamedBooleanOption(options, "ExportSolidModelRep");
            cache.CanExportSolidModelRep = canExportSolidModelRep != null ? canExportSolidModelRep.Value : false;

            // Set the phase we are exporting
            cache.ActivePhaseId = ElementId.InvalidElementId;
            
            String activePhaseElementValue;
            if (options.TryGetValue("ActivePhase", out activePhaseElementValue))
                cache.ActivePhaseId = ParseElementId(activePhaseElementValue);

            if ((cache.ActivePhaseId == ElementId.InvalidElementId) && (cache.FilterViewForExport != null))
            {
                Parameter currPhase = cache.FilterViewForExport.get_Parameter(BuiltInParameter.VIEW_PHASE);
                if (currPhase != null)
                    cache.ActivePhaseId = currPhase.AsElementId();
            }

            if (cache.ActivePhaseId == ElementId.InvalidElementId)
            {
                PhaseArray phaseArray = document.Phases;
                Phase lastPhase = phaseArray.get_Item(phaseArray.Size - 1);
                cache.ActivePhaseId = lastPhase.Id;
                cache.ActivePhaseElement = lastPhase;
            }
            else
            {
                cache.ActivePhaseElement = document.GetElement(cache.ActivePhaseId) as Phase;
            }

            // "FileType" - note - setting is not respected yet
            ParseFileType(options, cache);

            cache.SelectedConfigName = GetNamedStringOption(options, "ConfigName");

            bool? bExportLinks = GetNamedBooleanOption(options, "ExportingLinks");
            cache.ExportingLink = (bExportLinks.HasValue && bExportLinks.Value == true);

            if (cache.ExportingLink)
            {
                int? numInstances = GetNamedIntOption(options, "NumberOfExportedLinkInstances");
                for (int ii = 0; ii < numInstances; ii++)
                {
                    string optionName = (ii == 0) ? "ExportLinkInstanceTransform" : "ExportLinkInstanceTransform" + (ii + 1).ToString();
                    String aLinkInstanceTransform = GetNamedStringOption(options, optionName);

                    Transform currTransform = null;
                    if (!String.IsNullOrEmpty(aLinkInstanceTransform))
                    {
                        //reconstruct transform
                        Transform tr = ParseTransform(aLinkInstanceTransform);
                        //set to cache
                        if (tr != null)
                            currTransform = tr;
                    }

                    string fileName = null;

                    if (ii > 0)
                    {
                        optionName = "ExportLinkInstanceFileName" + (ii + 1).ToString();
                        fileName = GetNamedStringOption(options, optionName);
                    }

                    if (currTransform == null)
                        cache.m_LinkInstanceInfos.Add(new Tuple<string, Transform>(fileName, Transform.Identity));
                    else
                        cache.m_LinkInstanceInfos.Add(new Tuple<string, Transform>(fileName, currTransform));
                }
            }

            return cache;
        }

        /// <summary>
        /// Utility for processing boolean option from the options collection.
        /// </summary>
        /// <param name="options">The collection of named options for IFC export.</param>
        /// <param name="optionName">The name of the target option.</param>
        /// <returns>The value of the option, or null if the option is not set.</returns>
        public static bool? GetNamedBooleanOption(IDictionary<String, String> options, String optionName)
        {
            String optionString;
            if (options.TryGetValue(optionName, out optionString))
            {
                bool option;
                if (Boolean.TryParse(optionString, out option))
                    return option;
                
                // TODO: consider logging this error later and handling results better.
                throw new Exception("Option '" + optionName +"' could not be parsed to boolean");
            }
            return null;
        }

        /// <summary>
        /// Utility for processing integer option from the options collection.
        /// </summary>
        /// <param name="options">The collection of named options for IFC export.</param>
        /// <param name="optionName">The name of the target option.</param>
        /// <returns>The value of the option, or null if the option is not set.</returns>
        public static int? GetNamedIntOption(IDictionary<String, String> options, String optionName)
        {
            String optionString;
            if (options.TryGetValue(optionName, out optionString))
            {
                int option;
                if (int.TryParse(optionString, out option))
                    return option;

                // TODO: consider logging this error later and handling results better.
                throw new Exception("Option '" + optionName + "' could not be parsed to int");
            }
            return null;
        }

        /// <summary>
        /// Utility for processing string option from the options collection.
        /// </summary>
        /// <param name="options">The collection of named options for IFC export.</param>
        /// <param name="optionName">The name of the target option.</param>
        /// <returns>The value of the option, or null if the option is not set.</returns>
        private static string GetNamedStringOption(IDictionary<String, String> options, String optionName)
        {
            String optionString;
            options.TryGetValue(optionName, out optionString);
            return optionString;
        }

        /// <summary>
        /// Utility for parsing IFC file type.
        /// </summary>
        /// <remarks>
        /// If the file type can't be retrieved from the options collection, it will parse the file name extension.
        /// </remarks>
        /// <param name="options">The collection of named options for IFC export.</param>
        /// <param name="cache">The export options cache.</param>
        private static void ParseFileType(IDictionary<String, String> options, ExportOptionsCache cache)
        {            
            String fileTypeString;
            if (options.TryGetValue("FileType", out fileTypeString))
            {
                IFCFileFormat fileType;
                if (Enum.TryParse<IFCFileFormat>(fileTypeString, true, out fileType))
                {
                    cache.IFCFileFormat = fileType;
                }
                else
                {
                    // Error - the option supplied could not be mapped to ExportFileType.
                    // TODO: consider logging this error later and handling results better.
                    throw new Exception("Option 'FileType' did not match an existing IFCFileFormat value");
                }
            }
            else if (!string.IsNullOrEmpty(cache.FileName))
            {
                if (cache.FileName.EndsWith(".ifcXML")) //localization?
                {
                    cache.IFCFileFormat = IFCFileFormat.IfcXML;
                }
                else if (cache.FileName.EndsWith(".ifcZIP"))
                {
                    cache.IFCFileFormat = IFCFileFormat.IfcZIP;
                }
                else
                {
                    cache.IFCFileFormat = IFCFileFormat.Ifc;
                }
            }
        }

        /// <summary>
        /// The property set options.
        /// </summary>
        public PropertySetOptions PropertySetOptions
        {
            get;
            set;
        }

        /// <summary>
        /// The file version.
        /// </summary>
        public IFCVersion FileVersion
        {
            get;
            set;
        }

        /// <summary>
        /// The file name.
        /// </summary>
        public string FileName
        {
            get;
            set;
        }
        
        /// <summary>
        /// Identifies if the file version being exported is 2x2.
        /// </summary>
        public bool ExportAs2x2
        {
            get
            {
                return FileVersion == IFCVersion.IFC2x2 || FileVersion == IFCVersion.IFCBCA;
            }
        }

        /// <summary>
        /// Identifies if the file version being exported is 2x3 Coordination View 2.0.
        /// </summary>
        public bool ExportAs2x3CoordinationView2
        {
            get
            {
                return FileVersion == IFCVersion.IFC2x3CV2;
            }
        }

        /// <summary>
        /// Identifies if the file version being exported is 2x3 or 4 Coordination View 2.0.
        /// </summary>
        public bool ExportAsCoordinationView2
        {
            get
            {
                return ((FileVersion == IFCVersion.IFC2x3CV2) || (FileVersion == IFCVersion.IFC4));
            }
        }
        
        /// <summary>
        /// Identifies if the file version being exported is 4.
        /// </summary>
        public bool ExportAs4
        {
            get
            {
                return FileVersion == IFCVersion.IFC4;
            }
        }

        /// <summary>
        /// Cache variable for the export annotations override (if set independently via the UI or API inputs)
        /// </summary>
        private bool? ExportAnnotationsOverride
        {
            get;
            set;
        }

        /// <summary>
        /// Identifies if the file version being exported supports annotations.
        /// </summary>
        public bool ExportAnnotations
        {
            get
            {
                if (ExportAnnotationsOverride != null) 
                    return (bool)ExportAnnotationsOverride;
                return (!ExportAs2x2 && !ExportAsCoordinationView2);
            }
        }

        /// <summary>
        /// Identifies if we allow exporting advanced swept solids (vs. BReps if false).
        /// </summary>
        public bool ExportAdvancedSweptSolids
        {
            get;
            set;
        }

        /// <summary>
        /// Whether or not split walls and columns.
        /// </summary>
        public bool WallAndColumnSplitting
        {
            get;
            set;
        }

        /// <summary>
        /// Whether or not export base quantities.
        /// </summary>
        public bool ExportBaseQuantities
        {
            get;
            set;
        }

        /// <summary>
        /// The space boundary level.
        /// </summary>
        public int SpaceBoundaryLevel
        {
            get;
            set;
        }

        /// <summary>
        /// Whether or not export the Part element from host.
        /// Export Part element only if 'Current View Only' is checked and 'Show Parts' is selected. 
        /// </summary>
        public bool ExportParts
        {
            get;
            set;
        }

        /// <summary>
        /// Cache variable for the ExportPartsAsBuildingElements override (if set independently via the UI)
        /// </summary>
        public bool? ExportPartsAsBuildingElementsOverride
        {
            get;
            set;
        }

        /// <summary>
        /// Whether or not export the Parts as independent building elements.
        /// Only if allows export parts and 'Export parts as building elements' is selected. 
        /// </summary>
        public bool ExportPartsAsBuildingElements
        {
            get
            {
                if (ExportPartsAsBuildingElementsOverride != null)
                    return (bool)ExportPartsAsBuildingElementsOverride;
                return false;
            }
        }

        
        /// <summary>
        /// Cache variable for the ExportBoundingBox override (if set independently via the UI)
        /// </summary>
        public bool? ExportBoundingBoxOverride
        {
            get;
            set;
        }

        /// <summary>
        /// Whether or not export the bounding box.
        /// </summary>
        public bool ExportBoundingBox
        {
            get
            {
                // if the option is set by alternate UI, return the setting in UI.
                if (ExportBoundingBoxOverride != null)
                    return (bool)ExportBoundingBoxOverride;
                // otherwise export the bounding box only if it is GSA export.
                else if (FileVersion == IFCVersion.IFCCOBIE)
                    return true;
                return false;
            }
        }

        /// <summary>
        /// Whether or not include IFCSITE elevation in the site local placement origin.
        /// </summary>
        public bool IncludeSiteElevation
        {
            get;
            set;
        }

        /// <summary>
        /// Whether or not to use coarse tessellation for selected element types.
        /// This is currently: floors, railings, ramps, spaces, and stairs.
        /// </summary>
        public bool UseCoarseTessellation
        {
            get;
            set;
        }
        
        /// <summary>
        /// Cache variable for the Alternate UI version override (if export from Alternate UI)
        /// </summary>
        public string AlternateUIVersionOverride
        {
            get;
            set;
        }

        /// <summary>
        /// The UI Version of the exporter.
        /// </summary>
        public string ExporterUIVersion
        {
            get
            {
                if (AlternateUIVersionOverride != null)
                    return AlternateUIVersionOverride;
                else
                    return "Default UI";
            }
        }

        /// <summary>
        /// The version of the exporter.
        /// </summary>
        public string ExporterVersion
        {
            get
            {
                string assemblyFile = typeof(Revit.IFC.Export.Exporter.Exporter).Assembly.Location;
                string exporterVersion = "Unknown Exporter version";
                if (File.Exists(assemblyFile))
                {
                    exporterVersion = "Exporter " + FileVersionInfo.GetVersionInfo(assemblyFile).FileVersion;
                }
                return exporterVersion;
            }
        }

        /// <summary>
        /// A collection of elements from which to export (before filtering is applied).  If empty, all elements in the document
        /// are used as the initial set of elements before filtering is applied.
        /// </summary>
        public IList<ElementId> ElementsForExport
        {
            get;
            set;
        }

        /// <summary>
        /// The filter view for export.
        /// </summary>
        public View FilterViewForExport
        {
            get;
            set;
        }

        /// <summary>
        /// Whether or not to export all levels, or just export building stories.
        /// This will be set to true by default if there are no building stories in the file.
        /// </summary>
        public bool ExportAllLevels
        {
            get;
            set;
        }

        /// <summary>
        /// Determines how to generate space volumes on export.  True means that we use the 2D room boundary and extrude it upwards based
        /// on the room height.  This is the method used in 2x2 and by user option.  False means using the room geometry.  The user option
        /// is needed for certain governmental requirements, such as in Korea for non-residental buildings.
        /// </summary>
        public bool Use2DRoomBoundaryForRoomVolumeCreation
        {
            get;
            set;
        }

        /// <summary>
        /// Contains options for controlling how IFC GUIDs are generated on export.
        /// </summary>
        public GUIDOptions GUIDOptions
        {
            get
            {
                if (m_GUIDOptions == null)
                    m_GUIDOptions = new GUIDOptions();
                return m_GUIDOptions;
            }
        }

        /// <summary>
        /// Contains options for setting how entity names are generated.
        /// </summary>
        public NamingOptions NamingOptions
        {
            get;
            set;
        }

        /// <summary>
        /// The file format to export.  Not used currently.
        /// </summary>
        // TODO: Connect this to the output file being written by the client.
        public IFCFileFormat IFCFileFormat
        {
            get;
            set;
        }

        /// <summary>
        /// Select export Config Name from the UI
        /// </summary>
        public String SelectedConfigName
        {
            get;
            set;
        }

        /// <summary>
        /// Allow exporting a mix of extrusions and BReps as a solid model, if possible.
        /// </summary>
        public bool CanExportSolidModelRep { get; set; }

        /// <summary>
        /// Specifies which phase id to export.  May be expanded to phases.
        /// </summary>
        public ElementId ActivePhaseId
        {
            get;
            protected set;
        }

        /// <summary>
        /// The phase element corresponding to the phase id.
        /// </summary>
        public Phase ActivePhaseElement
        {
            get;
            protected set;
        }

        ///<summary>
        /// The ExportingLink flag.
        /// This stores the flag telling if the current export is for a linked document.
        /// </summary>
        public bool ExportingLink
        {
            get;
            set;
        }

        private IList<Tuple<string, Transform>> m_LinkInstanceInfos = new List<Tuple<string, Transform>>();

        /// <summary>
        /// Get the number of RevitLinkInstance transforms for this export.
        /// </summary>
        /// <returns>The number of Revit Link Instance transforms for this export.</returns>
        public int GetNumLinkInstanceInfos()
        {
            if (m_LinkInstanceInfos == null)
                return 0;

            return m_LinkInstanceInfos.Count;
        }

        /// <summary>
        /// Gets the file name of the link corresponding to the given index.
        /// </summary>
        /// <param name="idx">The index</param>
        /// <returns>The transform corresponding to the given index, or the Identity transform if out of range.</returns>
        /// <remarks>Note that the file name for index 0 is not stored here, and returns null.</remarks>
        public string GetLinkInstanceFileName(int idx)
        {
            if (idx < 1 || idx >= GetNumLinkInstanceInfos())
                return null;

            return m_LinkInstanceInfos[idx].Item1;
        }
        
        /// <summary>
        /// Gets the transform corresponding to the given index.
        /// </summary>
        /// <param name="idx">The index</param>
        /// <returns>The transform corresponding to the given index, or the Identity transform if out of range.</returns>
        public Transform GetLinkInstanceTransform(int idx)
        {
            if (idx < 0 || idx >= GetNumLinkInstanceInfos())
                return Transform.Identity;

            return m_LinkInstanceInfos[idx].Item2;
        }
    }
}