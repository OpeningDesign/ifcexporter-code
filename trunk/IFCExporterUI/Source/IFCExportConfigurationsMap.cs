﻿//
// BIM IFC export alternate UI library: this library works with Autodesk(R) Revit(R) to provide an alternate user interface for the export of IFC files from Revit.
// Copyright (C) 2012  Autodesk, Inc.
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

using Autodesk.Revit;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace BIM.IFC.Export.UI
{
    /// <summary>
    /// The map to store BuiltIn and Saved configurations.
    /// </summary>
    public class IFCExportConfigurationsMap
    {
        private Dictionary<String, IFCExportConfiguration> m_configurations = new Dictionary<String, IFCExportConfiguration>();
        private Schema m_schema = null;
        private Schema m_mapSchema = null;
        private static Guid s_schemaId = new Guid("A1E672E5-AC88-4933-A019-F9068402CFA7");
        private static Guid s_mapSchemaId = new Guid("DCB88B13-594F-44F6-8F5D-AE9477305AC3");

        /// <summary>
        /// Constructs a default map.
        /// </summary>
        public IFCExportConfigurationsMap()
        {
        }

        /// <summary>
        /// Constructs a new map as a copy of an existing one.
        /// </summary>
        /// <param name="map">The specific map to copy.</param>
        public IFCExportConfigurationsMap(IFCExportConfigurationsMap map)
        {
            // Deep copy
            foreach (IFCExportConfiguration value in map.Values)
            {
                Add(value.Clone());
            }
        }

        /// <summary>
        /// Adds the built-in configurations to the map.
        /// </summary>
        public void AddBuiltInConfigurations()
        {
            // These are the built-in configurations.  Provide a more extensible means of storage.
            // Order of construction: name, version, space boundaries, QTO, split walls, internal sets, 2d elems.
            Add(IFCExportConfiguration.CreateBuiltInConfiguration("Default 2x3", IFCVersion.IFC2x3, 1, false, false, true, true, true));
            Add(IFCExportConfiguration.CreateBuiltInConfiguration("Default 2x2", IFCVersion.IFC2x2, 1, false, false, true, false, true));
            Add(IFCExportConfiguration.CreateBuiltInConfiguration("BCA", IFCVersion.IFCBCA, 1, false, true, true, false, true));
            Add(IFCExportConfiguration.CreateBuiltInConfiguration("GSA", IFCVersion.IFCCOBIE, 2, true, true, true, true, true));
            Add(IFCExportConfiguration.CreateBuiltInConfiguration("Coordination View 2.0", IFCVersion.IFC2x3CV2, 0, false, true, false, false, false));
        }

        /// <summary>
        /// Adds the saved configuration from document to the map.
        /// </summary>
        /// <param name="document">The document storing the saved configuration.</param>
        public void AddSavedConfigurations(Document document)
        {
            try
            {
                if (m_schema == null)
                {
                    m_schema = Schema.Lookup(s_schemaId);
                }
                if (m_mapSchema == null)
                {
                    m_mapSchema = Schema.Lookup(s_mapSchemaId);
                }

                if (m_mapSchema != null)
                {
                    foreach (DataStorage storedSetup in GetSavedConfigurations(document, m_mapSchema))
                    {
                        Entity configEntity = storedSetup.GetEntity(m_mapSchema);
                        IDictionary<string, string> configMap = configEntity.Get<IDictionary<string, string>>(s_configMapField);
                        IFCExportConfiguration configuration = IFCExportConfiguration.CreateDefaultConfiguration();
                        if (configMap.ContainsKey(s_setupName))
                            configuration.Name = configMap[s_setupName];
                        if (configMap.ContainsKey(s_setupVersion))
                            configuration.IFCVersion = (IFCVersion)Enum.Parse(typeof(IFCVersion), configMap[s_setupVersion]);
                        if (configMap.ContainsKey(s_setupFileFormat))
                            configuration.IFCFileType = (IFCFileFormat)Enum.Parse(typeof(IFCFileFormat), configMap[s_setupFileFormat]);
                        if (configMap.ContainsKey(s_setupSpaceBoundaries))
                            configuration.SpaceBoundaries = int.Parse(configMap[s_setupSpaceBoundaries]);
                        if (configMap.ContainsKey(s_setupQTO))
                            configuration.ExportBaseQuantities = bool.Parse(configMap[s_setupQTO]);
                        if (configMap.ContainsKey(s_setupCurrentView))
                            configuration.VisibleElementsOfCurrentView = bool.Parse(configMap[s_setupCurrentView]);
                        if (configMap.ContainsKey(s_splitWallsAndColumns))
                            configuration.SplitWallsAndColumns = bool.Parse(configMap[s_splitWallsAndColumns]);
                        if (configMap.ContainsKey(s_setupExport2D))
                            configuration.Export2DElements = bool.Parse(configMap[s_setupExport2D]);
                        if (configMap.ContainsKey(s_setupExportRevitProps))
                            configuration.ExportInternalRevitPropertySets = bool.Parse(configMap[s_setupExportRevitProps]);
                        if (configMap.ContainsKey(s_setupExportIFCCommonProperty))
                            configuration.ExportIFCCommonPropertySets = bool.Parse(configMap[s_setupExportIFCCommonProperty]);
                        if (configMap.ContainsKey(s_setupUse2DForRoomVolume))
                            configuration.Use2DRoomBoundaryForVolume = bool.Parse(configMap[s_setupUse2DForRoomVolume]);
                        if (configMap.ContainsKey(s_setupUseFamilyAndTypeName))
                            configuration.UseFamilyAndTypeNameForReference = bool.Parse(configMap[s_setupUseFamilyAndTypeName]);
                        if (configMap.ContainsKey(s_setupExportPartsAsBuildingElements))
                            configuration.ExportPartsAsBuildingElements = bool.Parse(configMap[s_setupExportPartsAsBuildingElements]);
                        if (configMap.ContainsKey(s_setupExportSurfaceStyles))
                            configuration.ExportSurfaceStyles = bool.Parse(configMap[s_setupExportSurfaceStyles]);

                        Add(configuration);
                    }
                    return; // if finds the config in map schema, return and skip finding the old schema.
                }

                // find the config in old schema.
                if (m_schema != null)
                {
                    foreach (DataStorage storedSetup in GetSavedConfigurations(document, m_schema))
                    {
                        Entity configEntity = storedSetup.GetEntity(m_schema);
                        IFCExportConfiguration configuration = IFCExportConfiguration.CreateDefaultConfiguration();
                        configuration.Name = configEntity.Get<String>(s_setupName);
                        //configuration.Description = configEntity.Get<String>(s_setupDescription);
                        configuration.IFCVersion = (IFCVersion)configEntity.Get<int>(s_setupVersion);
                        configuration.IFCFileType = (IFCFileFormat)configEntity.Get<int>(s_setupFileFormat);
                        configuration.SpaceBoundaries = configEntity.Get<int>(s_setupSpaceBoundaries);
                        configuration.ExportBaseQuantities = configEntity.Get<bool>(s_setupQTO);
                        configuration.SplitWallsAndColumns = configEntity.Get<bool>(s_splitWallsAndColumns);
                        configuration.Export2DElements = configEntity.Get<bool>(s_setupExport2D);
                        configuration.ExportInternalRevitPropertySets = configEntity.Get<bool>(s_setupExportRevitProps);
                        Field fieldIFCCommonPropertySets = m_schema.GetField(s_setupExportIFCCommonProperty);
                        if (fieldIFCCommonPropertySets != null)
                            configuration.ExportIFCCommonPropertySets = configEntity.Get<bool>(s_setupExportIFCCommonProperty);
                        configuration.Use2DRoomBoundaryForVolume = configEntity.Get<bool>(s_setupUse2DForRoomVolume);
                        configuration.UseFamilyAndTypeNameForReference = configEntity.Get<bool>(s_setupUseFamilyAndTypeName);
                        Field fieldPartsAsBuildingElements = m_schema.GetField(s_setupExportPartsAsBuildingElements);
                        if (fieldPartsAsBuildingElements != null)
                            configuration.ExportPartsAsBuildingElements = configEntity.Get<bool>(s_setupExportPartsAsBuildingElements);
                        Field fieldSurfaceStyles = m_schema.GetField(s_setupExportSurfaceStyles);
                        if (fieldSurfaceStyles != null)
                            configuration.ExportSurfaceStyles = configEntity.Get<bool>(s_setupExportSurfaceStyles);

                        Add(configuration);
                    }
                }
            }
            catch (System.Exception)
            {
                // to avoid fail to show the dialog if any exception throws in reading schema.
            }
        }

        // The MapField is to defined the map<string,string> in schema. 
        // Please don't change the name values, it affects the schema.
        private const String s_configMapField = "MapField";
        // The following are the keys in the MapFied in new schema. For old schema, they are simple fields.
        private const String s_setupName = "Name";
        private const String s_setupDescription = "Description";
        private const String s_setupVersion = "Version";
        private const String s_setupFileFormat = "FileFormat";
        private const String s_setupSpaceBoundaries = "SpaceBoundaryLevel";
        private const String s_setupQTO = "ExportBaseQuantities";
        private const String s_splitWallsAndColumns = "SplitWallsAndColumns";
        private const String s_setupCurrentView = "VisibleElementsInCurrentView";
        private const String s_setupExport2D = "Export2DElements";
        private const String s_setupExportRevitProps = "ExportInternalRevitPropertySets";
        private const String s_setupExportIFCCommonProperty = "ExportIFCCommonPropertySets";
        private const String s_setupUse2DForRoomVolume = "Use2DBoundariesForRoomVolume";
        private const String s_setupUseFamilyAndTypeName = "UseFamilyAndTypeNameForReference";
        private const String s_setupExportPartsAsBuildingElements = "ExportPartsAsBuildingElements";
        private const String s_setupExportSurfaceStyles = "ExportSurfaceStyles";

        /// <summary>
        /// Updates the setups to save into the document.
        /// </summary>
        /// <param name="document">The document storing the saved configuration.</param>
        public void UpdateSavedConfigurations(Document document)
        {
            // delete the old schema and the DataStorage.
            if (m_schema == null)
            {
                m_schema = Schema.Lookup(s_schemaId);
            }
            if (m_schema != null)
            {
                IList<DataStorage> oldSavedConfigurations = GetSavedConfigurations(document, m_schema);
                if (oldSavedConfigurations.Count > 0)
                {
                    Transaction deleteTransaction = new Transaction(document, "Delete old IFC export setups");
                    deleteTransaction.Start();
                    List<ElementId> dataStorageToDelete = new List<ElementId>();
                    foreach (DataStorage dataStorage in oldSavedConfigurations)
                    {
                        dataStorageToDelete.Add(dataStorage.Id);
                    }
                    document.Delete(dataStorageToDelete);
                    deleteTransaction.Commit();
                }
            }

            // update the configurations to new map schema.
            if (m_mapSchema == null)
            {
                m_mapSchema = Schema.Lookup(s_mapSchemaId);
            }

            // Are there any setups to save or resave?
            List<IFCExportConfiguration> setupsToSave = new List<IFCExportConfiguration>();
            foreach (IFCExportConfiguration configuration in m_configurations.Values)
            {
                if (configuration.IsBuiltIn)
                    continue;

                // Store in-session settings in the cached in-session configuration
                if (configuration.IsInSession)
                {
                    IFCExportConfiguration.SetInSession(configuration);
                    continue;
                }

                setupsToSave.Add(configuration);
           }

           // If there are no setups to save, and if the schema is not present (which means there are no
           // previously existing setups which might have been deleted) we can skip the rest of this method.
            if (setupsToSave.Count <= 0 && m_mapSchema == null)
               return;

           if (m_mapSchema == null)
           {
               SchemaBuilder builder = new SchemaBuilder(s_mapSchemaId);
               builder.SetSchemaName("IFCExportConfigurationMap");
               builder.AddMapField(s_configMapField, typeof(String), typeof(String));
               m_mapSchema = builder.Finish();
           }

           // Overwrite all saved configs with the new list
           Transaction transaction = new Transaction(document, "Update IFC export setups");
           transaction.Start();
           IList<DataStorage> savedConfigurations = GetSavedConfigurations(document, m_mapSchema);
           int savedConfigurationCount = savedConfigurations.Count<DataStorage>();
           int savedConfigurationIndex = 0;
           foreach (IFCExportConfiguration configuration in setupsToSave)
           {
                DataStorage configStorage;
                if (savedConfigurationIndex >= savedConfigurationCount)
                {
                    configStorage = DataStorage.Create(document);
                }
                else
                {
                    configStorage = savedConfigurations[savedConfigurationIndex];
                    savedConfigurationIndex ++;
                }             

                Entity mapEntity = new Entity(m_mapSchema);
                IDictionary<string, string> mapData = new Dictionary<string, string>();
                mapData.Add(s_setupName, configuration.Name);
                mapData.Add(s_setupDescription, configuration.Description);
                mapData.Add(s_setupVersion, configuration.IFCVersion.ToString());
                mapData.Add(s_setupFileFormat, configuration.IFCFileType.ToString());
                mapData.Add(s_setupSpaceBoundaries, configuration.SpaceBoundaries.ToString());
                mapData.Add(s_setupQTO, configuration.ExportBaseQuantities.ToString());
                mapData.Add(s_setupCurrentView, configuration.VisibleElementsOfCurrentView.ToString());
                mapData.Add(s_splitWallsAndColumns, configuration.SplitWallsAndColumns.ToString());
                mapData.Add(s_setupExport2D, configuration.Export2DElements.ToString());
                mapData.Add(s_setupExportRevitProps, configuration.ExportInternalRevitPropertySets.ToString());
                mapData.Add(s_setupExportIFCCommonProperty, configuration.ExportIFCCommonPropertySets.ToString());
                mapData.Add(s_setupUse2DForRoomVolume, configuration.Use2DRoomBoundaryForVolume.ToString());
                mapData.Add(s_setupUseFamilyAndTypeName, configuration.UseFamilyAndTypeNameForReference.ToString());
                mapData.Add(s_setupExportPartsAsBuildingElements, configuration.ExportPartsAsBuildingElements.ToString());
                mapData.Add(s_setupExportSurfaceStyles, configuration.ExportSurfaceStyles.ToString());
                mapEntity.Set<IDictionary<string, String>>(s_configMapField, mapData);

                configStorage.SetEntity(mapEntity);
            }

            List<ElementId> elementsToDelete = new List<ElementId>();
            for (; savedConfigurationIndex < savedConfigurationCount; savedConfigurationIndex++)
            {
                DataStorage configStorage = savedConfigurations[savedConfigurationIndex];
                elementsToDelete.Add(configStorage.Id);
            }
            if (elementsToDelete.Count > 0)
                document.Delete(elementsToDelete);

            transaction.Commit();
        }

        /// <summary>
        /// Gets the saved setups from the document.
        /// </summary>
        /// <param name="document">The document storing the saved configuration.</param>
        /// <returns>The saved configurations.</returns>
        private IList<DataStorage> GetSavedConfigurations(Document document, Schema schema)
        {
            FilteredElementCollector collector = new FilteredElementCollector(document);
            collector.OfClass(typeof(DataStorage));
            Func<DataStorage, bool> hasTargetData = ds => (ds.GetEntity(schema) != null && ds.GetEntity(schema).IsValid());

            return collector.Cast<DataStorage>().Where<DataStorage>(hasTargetData).ToList<DataStorage>();
        }

        /// <summary>
        /// Adds a configuration to the map.
        /// </summary>
        /// <param name="configuration">The configuration.</param>
        public void Add(IFCExportConfiguration configuration)
        {
            m_configurations.Add(configuration.Name, configuration);
        }

        /// <summary>
        /// Remove a configuration by name.
        /// </summary>
        /// <param name="name">The name of configuration.</param>
        public void Remove(String name)
        {
            m_configurations.Remove(name);
        }

        /// <summary>
        /// Whether the map has the name of a configuration.
        /// </summary>
        /// <param name="name">The configuration name.</param>
        /// <returns>True for having the name, false otherwise.</returns>
        public bool HasName(String name)
        {
            if (name == null) return false;
            return m_configurations.ContainsKey(name);
        }

        /// <summary>
        /// The configuration by name.
        /// </summary>
        /// <param name="name">The name of a configuration.</param>
        /// <returns>The configuration of looking by name.</returns>
        public IFCExportConfiguration this[String name]
        {
            get
            {
                return m_configurations[name];
            }
        }

        /// <summary>
        /// The configurations in the map.
        /// </summary>
        public IEnumerable<IFCExportConfiguration> Values
        {
            get
            {
                return m_configurations.Values;
            }
        }
    }
}
