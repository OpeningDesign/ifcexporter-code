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

#pragma warning disable 0618

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.IFC;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.Exceptions;
using Revit.IFC.Export.Exporter.PropertySet;
using Revit.IFC.Export.Utility;
using System.Reflection;
using Revit.IFC.Common.Extensions;
using Revit.IFC.Export.Toolkit;
using Revit.IFC.Common.Utility;
using Revit.IFC.Common.Enums;
using Autodesk.Revit.DB.ExternalService;

namespace Revit.IFC.Export.Exporter
{
    /// <summary>
    /// This class implements the methods of interface IExternalDBApplication to register the IFC export client to Autodesk Revit.
    /// </summary>
    class ExporterApplication : IExternalDBApplication
    {
        #region IExternalDBApplication Members

        /// <summary>
        /// The method called when Autodesk Revit exits.
        /// </summary>
        /// <param name="application">Controlled application to be shutdown.</param>
        /// <returns>Return the status of the external application.</returns>
        public ExternalDBApplicationResult OnShutdown(Autodesk.Revit.ApplicationServices.ControlledApplication application)
        {
            return ExternalDBApplicationResult.Succeeded;
        }

        /// <summary>
        /// The method called when Autodesk Revit starts.
        /// </summary>
        /// <param name="application">Controlled application to be loaded to Autodesk Revit process.</param>
        /// <returns>Return the status of the external application.</returns>
        public ExternalDBApplicationResult OnStartup(Autodesk.Revit.ApplicationServices.ControlledApplication application)
        {
            // As an ExternalServer, the exporter cannot be registered until full application initialization. Setup an event callback to do this
            // at the appropriate time.
            application.ApplicationInitialized += OnApplicationInitialized;
            return ExternalDBApplicationResult.Succeeded;
        }

        #endregion

        /// <summary>
        /// The action taken on application initialization.
        /// </summary>
        /// <param name="sender">The sender.</param>
        /// <param name="eventArgs">The event args.</param>
        private void OnApplicationInitialized(object sender, EventArgs eventArgs)
        {
            SingleServerService service = ExternalServiceRegistry.GetService(ExternalServices.BuiltInExternalServices.IFCExporterService) as SingleServerService;
            if (service != null)
            {
                Exporter exporter = new Exporter();
                service.AddServer(exporter);
                service.SetActiveServer(exporter.GetServerId());
            }
            // TODO log this failure accordingly
        }
    }

    /// <summary>
    /// This class implements the method of interface IExporterIFC to perform an export to IFC.
    /// </summary>
    public class Exporter : IExporterIFC
    {
        // Used for debugging tool "WriteIFCExportedElements"
        private StreamWriter m_Writer;

        private IFCFile m_IfcFile;

        // Allow a derived class to add Element exporter routines.
        public delegate void ElementExporter(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document);
        
        protected ElementExporter m_ElementExporter = null;

        // Allow a derived class to add property sets.
        public delegate void PropertySetsToExport(IList<IList<PropertySetDescription>> propertySets, IFCVersion fileVersion);

        protected PropertySetsToExport m_PropertySetsToExport = null;

        // Allow a derived class to add quantities.
        public delegate void QuantitiesToExport(IList<IList<QuantityDescription>> propertySets, IFCVersion fileVersion);

        protected QuantitiesToExport m_QuantitiesToExport = null;

        #region IExporterIFC Members

        /// <summary>
        /// Create the list of element export routines.  Each routine will export a subset of Revit elements,
        /// allowing for a choice of which elements are exported, and in what order.
        /// This routine is protected, so it could be overriden by an Exporter class that inherits from this base class.
        /// </summary>
        protected virtual void InitializeElementExporters()
        {
            // Allow another function to potentially add exporters before ExportSpatialElements.
            if (m_ElementExporter == null)
                m_ElementExporter = ExportSpatialElements;
            else
                m_ElementExporter += ExportSpatialElements;
            m_ElementExporter += ExportNonSpatialElements;
            m_ElementExporter += ExportContainers;
            m_ElementExporter += ExportGrids;
            m_ElementExporter += ExportConnectors;
        }

        /// <summary>
        /// Implements the method that Autodesk Revit will invoke to perform an export to IFC.
        /// </summary>
        /// <param name="document">The document to export.</param>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="filterView">The view whose filter visibility settings govern the export.</param>
        public void ExportIFC(Autodesk.Revit.DB.Document document, ExporterIFC exporterIFC, Autodesk.Revit.DB.View filterView)
        {
            // Make sure our static caches are clear at the start, and end, of export.
            ExporterCacheManager.Clear();
            ExporterStateManager.Clear();

            try
            {
                BeginExport(exporterIFC, document, filterView);

                InitializeElementExporters();
                if (m_ElementExporter != null)
                    m_ElementExporter(exporterIFC, document);

                EndExport(exporterIFC, document);
            }
            finally
            {
                ExporterCacheManager.Clear();
                ExporterStateManager.Clear();

                DelegateClear();

                if (m_Writer != null)
                    m_Writer.Close();

                if (m_IfcFile != null)
                {
                    m_IfcFile.Close();
                    m_IfcFile = null;
                }
            }
        }

        public virtual string GetDescription()
        {
            return "IFC open source exporter";
        }

        public virtual string GetName()
        {
            return "IFC exporter";
        }

        public virtual Guid GetServerId()
        {
            return new Guid("BBE27F6B-E887-4F68-9152-1E664DAD29C3");
        }

        public virtual string GetVendorId()
        {
            return "IFCX";
        }

        // This is not virtual, and should not be overriden.
        public Autodesk.Revit.DB.ExternalService.ExternalServiceId GetServiceId()
        {
            return Autodesk.Revit.DB.ExternalService.ExternalServices.BuiltInExternalServices.IFCExporterService;
        }

        #endregion

        protected void ExportSpatialElements(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            ExportOptionsCache exportOptionsCache = ExporterCacheManager.ExportOptionsCache;
            View filterView = exportOptionsCache.FilterViewForExport;

            FilteredElementCollector spatialElementCollector;
            ICollection<ElementId> idsToExport = exportOptionsCache.ElementsForExport;
            if (idsToExport.Count > 0)
            {
                spatialElementCollector = new FilteredElementCollector(document, idsToExport);
            }
            else
            {
                spatialElementCollector = (filterView == null) ?
                    new FilteredElementCollector(document) : new FilteredElementCollector(document, filterView.Id);
            }

            ISet<ElementId> exportedSpaces = null;
            if (exportOptionsCache.SpaceBoundaryLevel == 2)
                exportedSpaces = SpatialElementExporter.ExportSpatialElement2ndLevel(this, exporterIFC, document);

            //export all spatial elements for no or 1st level room boundaries; for 2nd level, export spaces that couldn't be exported above.
            SpatialElementExporter.InitializeSpatialElementGeometryCalculator(document, exporterIFC);
            ElementFilter spatialElementFilter = ElementFilteringUtil.GetSpatialElementFilter(document, exporterIFC);
            spatialElementCollector.WherePasses(spatialElementFilter);
            foreach (Element element in spatialElementCollector)
            {
                if ((element == null) || (exportedSpaces != null && exportedSpaces.Contains(element.Id)))
                    continue;
                ExportElement(exporterIFC, element);
            }
        }

        protected void ExportNonSpatialElements(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            FilteredElementCollector otherElementCollector;
            View filterView = ExporterCacheManager.ExportOptionsCache.FilterViewForExport;

            ICollection<ElementId> idsToExport = ExporterCacheManager.ExportOptionsCache.ElementsForExport;
            if (idsToExport.Count > 0)
            {
                otherElementCollector = new FilteredElementCollector(document, idsToExport);
            }
            else
            {
                otherElementCollector = (filterView == null) ?
                    new FilteredElementCollector(document) : new FilteredElementCollector(document, filterView.Id);
            }

            ElementFilter nonSpatialElementFilter = ElementFilteringUtil.GetNonSpatialElementFilter(document, exporterIFC);
            otherElementCollector.WherePasses(nonSpatialElementFilter);
            foreach (Element element in otherElementCollector)
            {
                ExportElement(exporterIFC, element);
            }

        }

        /// <summary>
        /// Export various containers that depend on individual element export.
        /// </summary>
        /// <param name="document">The Revit document.</param>
        /// <param name="exporterIFC">The exporterIFC class.</param>
        protected void ExportContainers(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            using (ExporterStateManager.ForceElementExport forceElementExport = new ExporterStateManager.ForceElementExport())
            {
                ExportCachedRailings(exporterIFC, document);
                ExportCachedFabricAreas(exporterIFC, document);
                ExportTrusses(exporterIFC, document);
                ExportBeamSystems(exporterIFC, document);
                ExportAreaSchemes(exporterIFC, document);
                ExportZones(exporterIFC, document);
            }
        }

        /// <summary>
        /// Export railings cached during spatial element export.  
        /// Railings are exported last as their containment is not known until all stairs have been exported.
        /// This is a very simple sorting, and further containment issues could require a more robust solution in the future.
        /// </summary>
        /// <param name="document">The Revit document.</param>
        /// <param name="exporterIFC">The exporterIFC class.</param>
        protected void ExportCachedRailings(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            foreach (ElementId elementId in ExporterCacheManager.RailingCache)
            {
                Element element = document.GetElement(elementId);
                ExportElement(exporterIFC, element);
            }
        }

        /// <summary>
        /// Export FabricAreas cached during non-spatial element export.  
        /// We export whatever FabricAreas actually have handles as IfcGroup.
        /// </summary>
        /// <param name="document">The Revit document.</param>
        /// <param name="exporterIFC">The exporterIFC class.</param>
        protected void ExportCachedFabricAreas(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            foreach (ElementId elementId in ExporterCacheManager.FabricAreaHandleCache.Keys)
            {
                Element element = document.GetElement(elementId);
                ExportElement(exporterIFC, element);
            }
        }

        /// <summary>
        /// Export Trusses.  These could be in assemblies, so do before assembly export, but after beams and members are exported.
        /// </summary>
        /// <param name="document">The Revit document.</param>
        /// <param name="exporterIFC">The exporterIFC class.</param>
        protected void ExportTrusses(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            foreach (ElementId elementId in ExporterCacheManager.TrussCache)
            {
                Element element = document.GetElement(elementId);
                ExportElement(exporterIFC, element);
            }
        }

        /// <summary>
        /// Export BeamSystems.  These could be in assemblies, so do before assembly export, but after beams are exported.
        /// </summary>
        /// <param name="document">The Revit document.</param>
        /// <param name="exporterIFC">The exporterIFC class.</param>
        protected void ExportBeamSystems(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            foreach (ElementId elementId in ExporterCacheManager.BeamSystemCache)
            {
                Element element = document.GetElement(elementId);
                ExportElement(exporterIFC, element);
            }
        }

        /// <summary>
        /// Export Zones.
        /// </summary>
        /// <param name="document">The Revit document.</param>
        /// <param name="exporterIFC">The exporterIFC class.</param>
        protected void ExportZones(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            foreach (ElementId elementId in ExporterCacheManager.ZoneCache)
            {
                Element element = document.GetElement(elementId);
                ExportElement(exporterIFC, element);
            }
        }

        /// <summary>
        /// Export Area Schemes.
        /// </summary>
        /// <param name="document">The Revit document.</param>
        /// <param name="exporterIFC">The exporterIFC class.</param>
        protected void ExportAreaSchemes(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            foreach (ElementId elementId in ExporterCacheManager.AreaSchemeCache.Keys)
            {
                Element element = document.GetElement(elementId);
                ExportElement(exporterIFC, element);
            }
        }

        protected void ExportGrids(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            // Export the grids
            GridExporter.Export(exporterIFC, document);
        }

        protected void ExportConnectors(ExporterIFC exporterIFC, Autodesk.Revit.DB.Document document)
        {
            ConnectorExporter.Export(exporterIFC);
        }

        /// <summary>
        /// Determines if the selected element meets extra criteria for export.
        /// </summary>
        /// <param name="exporterIFC">The exporter class.</param>
        /// <param name="element">The current element to export.</param>
        /// <returns>True if the element should be exported.</returns>
        protected virtual bool CanExportElement(ExporterIFC exporterIFC, Autodesk.Revit.DB.Element element)
        {
            return ElementFilteringUtil.CanExportElement(exporterIFC, element, false);
        }

        /// <summary>
        /// Performs the export of elements, including spatial and non-spatial elements.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="element ">The element to export.</param>
        public virtual void ExportElement(ExporterIFC exporterIFC, Autodesk.Revit.DB.Element element)
        {
            if (!CanExportElement(exporterIFC, element))
                return;

            //WriteIFCExportedElements
            if (m_Writer != null)
            {
                Category category = element.Category;
                m_Writer.WriteLine(String.Format("{0},{1},{2}", element.Id, category == null ? "null" : category.Name, element.GetType().Name));
            }

            try
            {
                using (ProductWrapper productWrapper = ProductWrapper.Create(exporterIFC, true))
                {
                    ExportElementImpl(exporterIFC, element, productWrapper);
                    ExporterUtil.ExportRelatedProperties(exporterIFC, element, productWrapper);
                }

                // We are going to clear the parameter cache for the element (not the type) after the export.
                // We do not expect to need the parameters for this element again, so we can free up the space.
                if (!(element is ElementType) && !ExporterStateManager.ShouldPreserveElementParameterCache(element))
                    ParameterUtil.RemoveElementFromCache(element);
            }
            catch (System.Exception ex)
            {
                HandleUnexpectedException(ex, exporterIFC, element);
            }
        }

        /// <summary>
        /// Handles the unexpected Exception.
        /// </summary>
        /// <param name="ex">The unexpected exception.</param>
        /// <param name="element ">The element got the exception.</param>
        internal void HandleUnexpectedException(Exception exception, ExporterIFC exporterIFC, Element element)
        {
            Document document = element.Document;
            string errMsg = String.Format("IFC error: Exporting element \"{0}\",{1} - {2}", element.Name, element.Id, exception.ToString());
            element.Document.Application.WriteJournalComment(errMsg, true);

            if (!ExporterUtil.IsFatalException(document, exception))
            {
                FailureMessage fm = new FailureMessage(BuiltInFailures.ExportFailures.IFCGenericExportWarning);
                fm.SetFailingElement(element.Id);
                document.PostFailure(fm);
            }
            else
            {
                // This exception should be rethrown back to the main Revit application.
                throw exception;
            }
        }

        /// <summary>
        /// Checks if the element is MEP type.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="element">The element to check.</param>
        /// <returns>True for MEP type of elements.</returns>
        private bool IsMEPType(ExporterIFC exporterIFC, Element element, IFCExportType exportType)
        {
            return ElementFilteringUtil.IsMEPType(exportType);
        }

        /// <summary>
        /// Checks if exporting an element as building elment proxy.
        /// </summary>
        /// <param name="element">The element.</param>
        /// <returns>True for exporting as proxy element.</returns>
        private bool ExportAsProxy(Element element, IFCExportType exportType)
        {
            // FaceWall should be exported as IfcWall.
            return ((element is FaceWall) || (element is ModelText) || (exportType == IFCExportType.IfcBuildingElementProxy));
        }

        /// <summary>
        /// Checks if exporting an element of Stairs category.
        /// </summary>
        /// <param name="element">The element.</param>
        /// <returns>True if element is of category OST_Stairs.</returns>
        private bool IsStairs(Element element)
        {
            return (CategoryUtil.GetSafeCategoryId(element) == new ElementId(BuiltInCategory.OST_Stairs));
        }

        /// <summary>
        /// Implements the export of element.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="element">The element to export.</param>
        /// <param name="productWrapper">The ProductWrapper object.</param>
        public virtual void ExportElementImpl(ExporterIFC exporterIFC, Element element, ProductWrapper productWrapper)
        {
            Options options;
            View ownerView = element.Document.GetElement(element.OwnerViewId) as View;
            if (ownerView == null)
            {
                options = GeometryUtil.GetIFCExportGeometryOptions();
            }
            else
            {
                options = new Options();
                options.View = ownerView;
            }
            GeometryElement geomElem = element.get_Geometry(options);

            // Default: we don't preserve the element parameter cache after export.
            bool shouldPreserveParameterCache = false;

            try
            {
                exporterIFC.PushExportState(element, geomElem);

                using (SubTransaction st = new SubTransaction(element.Document))
                {
                    st.Start();

                    // A long list of supported elements.  Please keep in alphabetical order.
                    if (element is AreaReinforcement || element is PathReinforcement || element is Rebar)
                    {
                        RebarExporter.Export(exporterIFC, element, productWrapper);
                    }
                    else if (element is AreaScheme)
                    {
                        AreaSchemeExporter.ExportAreaScheme(exporterIFC, element as AreaScheme, productWrapper);
                    }
                    else if (element is AssemblyInstance)
                    {
                        AssemblyInstance assemblyInstance = element as AssemblyInstance;
                        AssemblyInstanceExporter.ExportAssemblyInstanceElement(exporterIFC, assemblyInstance, productWrapper);
                    }
                    else if (element is BeamSystem)
                    {
                        if (ExporterCacheManager.BeamSystemCache.Contains(element.Id))
                            AssemblyInstanceExporter.ExportBeamSystem(exporterIFC, element as BeamSystem, productWrapper);
                        else
                        {
                            ExporterCacheManager.BeamSystemCache.Add(element.Id);
                            shouldPreserveParameterCache = true;
                        }
                    }
                    else if (element is Ceiling)
                    {
                        Ceiling ceiling = element as Ceiling;
                        CeilingExporter.ExportCeilingElement(exporterIFC, ceiling, geomElem, productWrapper);
                    }
                    else if (element is CeilingAndFloor || element is Floor)
                    {
                        // This covers both Floors and Building Pads.
                        HostObject hostObject = element as HostObject;
                        FloorExporter.Export(exporterIFC, hostObject, geomElem, productWrapper);
                    }
                    else if (element is ContFooting)
                    {
                        ContFooting footing = element as ContFooting;
                        FootingExporter.ExportFootingElement(exporterIFC, footing, geomElem, productWrapper);
                    }
                    else if (element is CurveElement)
                    {
                        CurveElement curveElem = element as CurveElement;
                        CurveElementExporter.ExportCurveElement(exporterIFC, curveElem, geomElem, productWrapper);
                    }
                    else if (element is CurtainSystem)
                    {
                        CurtainSystem curtainSystem = element as CurtainSystem;
                        CurtainSystemExporter.ExportCurtainSystem(exporterIFC, curtainSystem, productWrapper);
                    }
                    else if (CurtainSystemExporter.IsLegacyCurtainElement(element))
                    {
                        CurtainSystemExporter.ExportLegacyCurtainElement(exporterIFC, element, productWrapper);
                    }
                    else if (element is DuctInsulation)
                    {
                        DuctInsulation ductInsulation = element as DuctInsulation;
                        DuctInsulationExporter.ExportDuctInsulation(exporterIFC, ductInsulation, geomElem, productWrapper);
                    }
                    else if (element is DuctLining)
                    {
                        DuctLining ductLining = element as DuctLining;
                        DuctLiningExporter.ExportDuctLining(exporterIFC, ductLining, geomElem, productWrapper);
                    }
                    else if (element is ElectricalSystem)
                    {
                        ExporterCacheManager.SystemsCache.AddElectricalSystem(element.Id);
                    }
                    else if (element is FabricArea)
                    {
                        // We are exporting the fabric area as a group only.
                        FabricSheetExporter.ExportFabricArea(exporterIFC, element, productWrapper);
                    }
                    else if (element is FabricSheet)
                    {
                        FabricSheet fabricSheet = element as FabricSheet;
                        FabricSheetExporter.ExportFabricSheet(exporterIFC, fabricSheet, geomElem, productWrapper);
                    }
                    else if (element is FaceWall)
                    {
                        WallExporter.ExportWall(exporterIFC, element, geomElem, productWrapper);
                    }
                    else if (element is FamilyInstance)
                    {
                        FamilyInstance familyInstanceElem = element as FamilyInstance;
                        FamilyInstanceExporter.ExportFamilyInstanceElement(exporterIFC, familyInstanceElem, geomElem, productWrapper);
                    }
                    else if (element is FilledRegion)
                    {
                        FilledRegion filledRegion = element as FilledRegion;
                        FilledRegionExporter.Export(exporterIFC, filledRegion, geomElem, productWrapper);
                    }
                    else if (element is Grid)
                    {
                        ExporterCacheManager.GridCache.Add(element);
                    }
                    else if (element is Group)
                    {
                        Group group = element as Group;
                        GroupExporter.ExportGroupElement(exporterIFC, group, productWrapper);
                    }
                    else if (element is HostedSweep)
                    {
                        HostedSweep hostedSweep = element as HostedSweep;
                        HostedSweepExporter.Export(exporterIFC, hostedSweep, geomElem, productWrapper);
                    }
                    else if (element is Part)
                    {
                        Part part = element as Part;
                        if (ExporterCacheManager.ExportOptionsCache.ExportPartsAsBuildingElements)
                            PartExporter.ExportPartAsBuildingElement(exporterIFC, part, geomElem, productWrapper);
                        else
                            PartExporter.ExportStandalonePart(exporterIFC, part, geomElem, productWrapper);
                    }
                    else if (element is PipeInsulation)
                    {
                        PipeInsulation pipeInsulation = element as PipeInsulation;
                        PipeInsulationExporter.ExportPipeInsulation(exporterIFC, pipeInsulation, geomElem, productWrapper);
                    }
                    else if (element is Railing)
                    {
                        if (ExporterCacheManager.RailingCache.Contains(element.Id))
                            RailingExporter.ExportRailingElement(exporterIFC, element as Railing, productWrapper);
                        else
                        {
                            ExporterCacheManager.RailingCache.Add(element.Id);
                            RailingExporter.AddSubElementsToCache(element as Railing);
                            shouldPreserveParameterCache = true;
                        }
                    }
                    else if (RampExporter.IsRamp(element))
                    {
                        RampExporter.Export(exporterIFC, element, geomElem, productWrapper);
                    }
                    else if (element is RoofBase)
                    {
                        RoofBase roofElement = element as RoofBase;
                        RoofExporter.Export(exporterIFC, roofElement, geomElem, productWrapper);
                    }
                    else if (element is SpatialElement)
                    {
                        SpatialElement spatialElem = element as SpatialElement;
                        SpatialElementExporter.ExportSpatialElement(exporterIFC, spatialElem, productWrapper);
                    }
                    else if (IsStairs(element))
                    {
                        StairsExporter.Export(exporterIFC, element, geomElem, productWrapper);
                    }
                    else if (element is TextNote)
                    {
                        TextNote textNote = element as TextNote;
                        TextNoteExporter.Export(exporterIFC, textNote, productWrapper);
                    }
                    else if (element is TopographySurface)
                    {
                        TopographySurface topSurface = element as TopographySurface;
                        SiteExporter.ExportTopographySurface(exporterIFC, topSurface, geomElem, productWrapper);
                    }
                    else if (element is Truss)
                    {
                        if (ExporterCacheManager.TrussCache.Contains(element.Id))
                            AssemblyInstanceExporter.ExportTrussElement(exporterIFC, element as Truss, productWrapper);
                        else
                        {
                            ExporterCacheManager.TrussCache.Add(element.Id);
                            shouldPreserveParameterCache = true;
                        }
                    }
                    else if (element is Wall)
                    {
                        Wall wallElem = element as Wall;
                        WallExporter.Export(exporterIFC, wallElem, geomElem, productWrapper);
                    }
                    else if (element is WallSweep)
                    {
                        WallSweep wallSweep = element as WallSweep;
                        WallSweepExporter.Export(exporterIFC, wallSweep, geomElem, productWrapper);
                    }
                    else if (element is Zone)
                    {
                        if (ExporterCacheManager.ZoneCache.Contains(element.Id))
                            ZoneExporter.ExportZone(exporterIFC, element as Zone, productWrapper);
                        else
                        {
                            ExporterCacheManager.ZoneCache.Add(element.Id);
                            shouldPreserveParameterCache = true;
                        }
                    }
                    else
                    {
                        string ifcEnumType;
                        IFCExportType exportType = ExporterUtil.GetExportType(exporterIFC, element, out ifcEnumType);

                        bool exported = false;
                        if (IsMEPType(exporterIFC, element, exportType))
                            exported = GenericMEPExporter.Export(exporterIFC, element, geomElem, exportType, ifcEnumType, productWrapper);
                        else if (ExportAsProxy(element, exportType))
                            exported = ProxyElementExporter.Export(exporterIFC, element, geomElem, productWrapper);

                        // For ducts and pipes, we will add a IfcRelCoversBldgElements during the end of export.
                        if (exported && (element is Duct || element is Pipe))
                            ExporterCacheManager.MEPCache.CoveredElementsCache.Add(element.Id);
                    }

                    if (element.AssemblyInstanceId != ElementId.InvalidElementId)
                        ExporterCacheManager.AssemblyInstanceCache.RegisterElements(element.AssemblyInstanceId, productWrapper);
                    if (element.GroupId != ElementId.InvalidElementId)
                        ExporterCacheManager.GroupCache.RegisterElements(element.GroupId, productWrapper);

                    st.RollBack();
                }
            }
            finally
            {
                exporterIFC.PopExportState();
                ExporterStateManager.PreserveElementParameterCache(element, shouldPreserveParameterCache);
            }
        }

        /// <summary>
        /// Sets the schema information for the current export options.  This can be overridden.
        /// </summary>
        protected virtual IFCFileModelOptions CreateIFCFileModelOptions(ExporterIFC exporterIFC)
        {
            IFCFileModelOptions modelOptions = new IFCFileModelOptions();
            if (ExporterCacheManager.ExportOptionsCache.ExportAs2x2)
            {
                modelOptions.SchemaFile = Path.Combine(ExporterUtil.RevitProgramPath, "EDM\\IFC2X2_ADD1.exp");
                modelOptions.SchemaName = "IFC2x2_FINAL";
            }
            else if (ExporterCacheManager.ExportOptionsCache.ExportAs4)
            {
                modelOptions.SchemaFile = Path.Combine(ExporterUtil.RevitProgramPath, "EDM\\IFC4.exp");
                modelOptions.SchemaName = "IFC4";
            }
            else
            {
                // We leave IFC2x3 as default until IFC4 is finalized and generally supported across platforms.
                modelOptions.SchemaFile = Path.Combine(ExporterUtil.RevitProgramPath, "EDM\\IFC2X3_TC1.exp");
                modelOptions.SchemaName = "IFC2x3";
            }
            return modelOptions;
        }

        /// <summary>
        /// Sets the lists of property sets to be exported.  This can be overriden.
        /// </summary>
        protected virtual void InitializePropertySets(IFCVersion fileVersion)
        {
            ExporterInitializer.InitPropertySets(m_PropertySetsToExport, ExporterCacheManager.ExportOptionsCache.FileVersion);
        }

        /// <summary>
        /// Sets the lists of quantities to be exported.  This can be overriden.
        /// </summary>
        protected virtual void InitializeQuantities(IFCVersion fileVersion)
        {
            ExporterInitializer.InitQuantities(m_QuantitiesToExport, ExporterCacheManager.ExportOptionsCache.FileVersion, ExporterCacheManager.ExportOptionsCache.ExportBaseQuantities);
        }

        /// <summary>
        /// Initializes the common properties at the beginning of the export process.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="document">The document to export.</param>
        private void BeginExport(ExporterIFC exporterIFC, Document document, Autodesk.Revit.DB.View filterView)
        {
            // cache options
            ExportOptionsCache exportOptionsCache = ExportOptionsCache.Create(exporterIFC, filterView);
            ExporterCacheManager.ExportOptionsCache = exportOptionsCache;

            // Set language.
            Application app = document.Application;
            string pathName = document.PathName;
            LanguageType langType = LanguageType.Unknown;
            if (!String.IsNullOrEmpty(pathName))
            {
                try
                {
                    BasicFileInfo basicFileInfo = BasicFileInfo.Extract(pathName);
                    if (basicFileInfo != null)
                        langType = basicFileInfo.LanguageWhenSaved;
                }
                catch
                {
                }
            }
            if (langType == LanguageType.Unknown)
                langType = app.Language;
            ExporterCacheManager.LanguageType = langType;

            ElementFilteringUtil.InitCategoryVisibilityCache();

            ExporterCacheManager.Document = document;
            String writeIFCExportedElementsVar = Environment.GetEnvironmentVariable("WriteIFCExportedElements");
            if (writeIFCExportedElementsVar != null && writeIFCExportedElementsVar.Length > 0)
            {
                m_Writer = new StreamWriter(@"c:\ifc-output-filters.txt");
            }

            IFCFileModelOptions modelOptions = CreateIFCFileModelOptions(exporterIFC);

            m_IfcFile = IFCFile.Create(modelOptions);
            exporterIFC.SetFile(m_IfcFile);

            //init common properties
            InitializePropertySets(ExporterCacheManager.ExportOptionsCache.FileVersion);
            InitializeQuantities(ExporterCacheManager.ExportOptionsCache.FileVersion);

            IFCFile file = exporterIFC.GetFile();
            using (IFCTransaction transaction = new IFCTransaction(file))
            {
                // create building
                IFCAnyHandle applicationHandle = CreateApplicationInformation(file, document);

                CreateGlobalCartesianOrigin(exporterIFC);
                CreateGlobalDirection(exporterIFC);
                CreateGlobalDirection2D(exporterIFC);

                // Start out relative to nothing, but replace with site later.
                IFCAnyHandle relativePlacement = ExporterUtil.CreateAxis2Placement3D(file);
                IFCAnyHandle buildingPlacement = IFCInstanceExporter.CreateLocalPlacement(file, null, relativePlacement);

                CreateProject(exporterIFC, document, applicationHandle);

                IFCAnyHandle ownerHistory = exporterIFC.GetOwnerHistoryHandle();
                ProjectInfo projectInfo = document.ProjectInformation;

                string buildingName = String.Empty;
                string buildingDescription = null;
                string buildingLongName = null;
                if (projectInfo != null)
                {
                    try
                    {
                        buildingName = projectInfo.BuildingName;
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                    {
                    }
                    buildingDescription = NamingUtil.GetOverrideStringValue(projectInfo, "BuildingDescription", null);
                    buildingLongName = NamingUtil.GetOverrideStringValue(projectInfo, "BuildingLongName", buildingName);
                }
                
                IFCAnyHandle buildingAddress = CreateIFCAddress(file, document, projectInfo);

                string buildingGUID = GUIDUtil.CreateProjectLevelGUID(document, IFCProjectLevelGUIDType.Building);
                IFCAnyHandle buildingHandle = IFCInstanceExporter.CreateBuilding(file,
                    buildingGUID, ownerHistory, buildingName, buildingDescription, null, buildingPlacement, null, buildingLongName,
                    Toolkit.IFCElementComposition.Element, null, null, buildingAddress);
                ExporterCacheManager.BuildingHandle = buildingHandle;

                // create levels
                List<Level> levels = LevelUtil.FindAllLevels(document);

                bool exportAllLevels = true;
                for (int ii = 0; ii < levels.Count && exportAllLevels; ii++)
                {
                    Level level = levels[ii];
                    Parameter isBuildingStorey = level.get_Parameter(BuiltInParameter.LEVEL_IS_BUILDING_STORY);
                    if (isBuildingStorey == null || (isBuildingStorey.AsInteger() != 0))
                    {
                        exportAllLevels = false;
                        break;
                    }
                }

                IList<Element> unassignedBaseLevels = new List<Element>();

                ExporterCacheManager.ExportOptionsCache.ExportAllLevels = exportAllLevels;
                double lengthScale = UnitUtil.ScaleLengthForRevitAPI();

                IFCAnyHandle prevBuildingStorey = null;
                IFCAnyHandle prevPlacement = null;
                double prevHeight = 0.0;
                double prevElev = 0.0;

                for (int ii = 0; ii < levels.Count; ii++)
                {
                    Level level = levels[ii];
                    if (level == null)
                        continue;

                    IFCLevelInfo levelInfo = null;

                    if (!LevelUtil.IsBuildingStory(level))
                    {
                        if (prevBuildingStorey == null)
                            unassignedBaseLevels.Add(level);
                        else
                        {
                            levelInfo = IFCLevelInfo.Create(prevBuildingStorey, prevPlacement, prevHeight, prevElev, lengthScale, true);
                            ExporterCacheManager.LevelInfoCache.AddLevelInfo(exporterIFC, level.Id, levelInfo);
                        }
                        continue;
                    }

                    // When exporting to IFC 2x3, we have a limited capability to export some Revit view-specific
                    // elements, specifically Filled Regions and Text.  However, we do not have the
                    // capability to choose which views to export.  As such, we will choose (up to) one DBView per
                    // exported level.
                    // TODO: Let user choose which view(s) to export.  Ensure that the user know that only one view
                    // per level is supported.
                    View view = LevelUtil.FindViewByLevel(document, ViewType.FloorPlan, level);
                    if (view != null)
                    {
                        ExporterCacheManager.DBViewsToExport[view.Id] = level.Id;
                    }

                    double elev = level.ProjectElevation;
                    double height = 0.0;
                    List<ElementId> coincidentLevels = new List<ElementId>();
                    for (int jj = ii + 1; jj < levels.Count; jj++)
                    {
                        Level nextLevel = levels[jj];
                        if (!LevelUtil.IsBuildingStory(nextLevel))
                            continue;

                        double nextElev = nextLevel.ProjectElevation;
                        if (!MathUtil.IsAlmostEqual(nextElev, elev))
                        {
                            height = nextElev - elev;
                            break;
                        }
                        else if (ExporterCacheManager.ExportOptionsCache.WallAndColumnSplitting)
                            coincidentLevels.Add(nextLevel.Id);
                    }

                    double elevation = UnitUtil.ScaleLength(elev);
                    XYZ orig = new XYZ(0.0, 0.0, elevation);

                    IFCAnyHandle placement = ExporterUtil.CreateLocalPlacement(file, buildingPlacement, orig, null, null);
                    string levelName = NamingUtil.GetNameOverride(level, level.Name);
                    string objectType = NamingUtil.GetObjectTypeOverride(level, null);
                    string description = NamingUtil.GetDescriptionOverride(level, null);
                    string longName = level.Name;
                    string levelGUID = GUIDUtil.GetLevelGUID(level);
                    IFCAnyHandle buildingStorey = IFCInstanceExporter.CreateBuildingStorey(file,
                        levelGUID, exporterIFC.GetOwnerHistoryHandle(),
                        levelName, objectType, description, placement,
                        null, longName, Toolkit.IFCElementComposition.Element, elevation);

                    if (prevBuildingStorey == null)
                    {
                        foreach (Level baseLevel in unassignedBaseLevels)
                        {
                            levelInfo = IFCLevelInfo.Create(buildingStorey, placement, height, elev, lengthScale, true);
                            ExporterCacheManager.LevelInfoCache.AddLevelInfo(exporterIFC, baseLevel.Id, levelInfo);
                        }
                    }
                    prevBuildingStorey = buildingStorey;
                    prevPlacement = placement;
                    prevHeight = height;
                    prevElev = elev;

                    levelInfo = IFCLevelInfo.Create(buildingStorey, placement, height, elev, lengthScale, true);
                    ExporterCacheManager.LevelInfoCache.AddLevelInfo(exporterIFC, level.Id, levelInfo);

                    // if we have coincident levels, add buildingstoreys for them but use the old handle.
                    for (int jj = 0; jj < coincidentLevels.Count; jj++)
                    {
                        level = levels[ii + jj + 1];
                        levelInfo = IFCLevelInfo.Create(buildingStorey, placement, height, elev, lengthScale, true);
                        ExporterCacheManager.LevelInfoCache.AddLevelInfo(exporterIFC, level.Id, levelInfo);
                    }

                    ii += coincidentLevels.Count;

                    // We will export element properties, quantities and classifications when we decide to keep the level - we may delete it later.
                }
                transaction.Commit();
            }
        }

        private void GetElementHandles(ICollection<ElementId> ids, ISet<IFCAnyHandle> handles)
        {
            if (ids != null)
            {
                foreach (ElementId id in ids)
                {
                    IFCAnyHandle handle = ExporterCacheManager.ElementToHandleCache.Find(id);
                    if (!IFCAnyHandleUtil.IsNullOrHasNoValue(handle))
                        handles.Add(handle);
                }
            }
        }

        /// <summary>
        /// Completes the export process by writing information stored incrementally during export to the file.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="document">The document to export.</param>
        private void EndExport(ExporterIFC exporterIFC, Document document)
        {
            IFCFile file = exporterIFC.GetFile();
            IFCAnyHandle ownerHistory = exporterIFC.GetOwnerHistoryHandle();
                            
            using (IFCTransaction transaction = new IFCTransaction(file))
            {
                // In some cases, like multi-story stairs and ramps, we may have the same Pset used for multiple levels.
                // If ifcParams is null, re-use the property set.
                ISet<string> locallyUsedGUIDs = new HashSet<string>();
                
                // Relate Ducts and Pipes to their coverings (insulations and linings)
                foreach (ElementId ductOrPipeId in ExporterCacheManager.MEPCache.CoveredElementsCache)
                {
                    IFCAnyHandle ductOrPipeHandle = ExporterCacheManager.MEPCache.Find(ductOrPipeId);
                    if (IFCAnyHandleUtil.IsNullOrHasNoValue(ductOrPipeHandle))
                        continue;

                    HashSet<IFCAnyHandle> coveringHandles = new HashSet<IFCAnyHandle>();

                    try
                    {
                        ICollection<ElementId> liningIds = InsulationLiningBase.GetLiningIds(document, ductOrPipeId);
                        GetElementHandles(liningIds, coveringHandles);
                    }
                    catch
                    {
                    }

                    try
                    {
                        ICollection<ElementId> insulationIds = InsulationLiningBase.GetInsulationIds(document, ductOrPipeId);
                        GetElementHandles(insulationIds, coveringHandles);
                    }
                    catch
                    {
                    }

                    if (coveringHandles.Count > 0)
                        IFCInstanceExporter.CreateRelCoversBldgElements(file, GUIDUtil.CreateGUID(), ownerHistory, null, null, ductOrPipeHandle, coveringHandles);
                }

                // Relate stair components to stairs
                foreach (KeyValuePair<ElementId, StairRampContainerInfo> stairRamp in ExporterCacheManager.StairRampContainerInfoCache)
                {
                    StairRampContainerInfo stairRampInfo = stairRamp.Value;

                    IList<IFCAnyHandle> hnds = stairRampInfo.StairOrRampHandles;
                    for (int ii = 0; ii < hnds.Count; ii++)
                    {
                        IFCAnyHandle hnd = hnds[ii];
                        if (IFCAnyHandleUtil.IsNullOrHasNoValue(hnd))
                            continue;

                        IList<IFCAnyHandle> comps = stairRampInfo.Components[ii];
                        if (comps.Count == 0)
                            continue;

                        Element elem = document.GetElement(stairRamp.Key);
                        string guid = GUIDUtil.CreateSubElementGUID(elem, (int)IFCStairSubElements.ContainmentRelation);
                        if (locallyUsedGUIDs.Contains(guid))
                            guid = GUIDUtil.CreateGUID();
                        else
                            locallyUsedGUIDs.Add(guid);

                        ExporterUtil.RelateObjects(exporterIFC, guid, hnd, comps);
                    }
                }

                ProjectInfo projectInfo = document.ProjectInformation;
                IFCAnyHandle buildingHnd = ExporterCacheManager.BuildingHandle;

                // relate assembly elements to assemblies
                foreach (KeyValuePair<ElementId, AssemblyInstanceInfo> assemblyInfoEntry in ExporterCacheManager.AssemblyInstanceCache)
                {
                    AssemblyInstanceInfo assemblyInfo = assemblyInfoEntry.Value;
                    if (assemblyInfo == null)
                        continue;

                    IFCAnyHandle assemblyInstanceHandle = assemblyInfo.AssemblyInstanceHandle;
                    HashSet<IFCAnyHandle> elementHandles = assemblyInfo.ElementHandles;
                    if (elementHandles != null && assemblyInstanceHandle != null && elementHandles.Contains(assemblyInstanceHandle))
                        elementHandles.Remove(assemblyInstanceHandle);

                    if (assemblyInstanceHandle != null && elementHandles != null && elementHandles.Count != 0)
                    {
                        Element assemblyInstance = document.GetElement(assemblyInfoEntry.Key);
                        string guid = GUIDUtil.CreateSubElementGUID(assemblyInstance, (int)IFCAssemblyInstanceSubElements.RelContainedInSpatialStructure);

                        if (IFCAnyHandleUtil.IsSubTypeOf(assemblyInstanceHandle, IFCEntityType.IfcSystem))
                        {
                            IFCInstanceExporter.CreateRelAssignsToGroup(file, guid, ownerHistory, null, null, elementHandles, null, assemblyInstanceHandle);
                        }
                        else
                        {
                            ExporterUtil.RelateObjects(exporterIFC, guid, assemblyInstanceHandle, elementHandles);
                            // Set the PlacementRelTo of assembly elements to assembly instance.
                            IFCAnyHandle assemblyPlacement = IFCAnyHandleUtil.GetObjectPlacement(assemblyInstanceHandle);
                            AssemblyInstanceExporter.SetLocalPlacementsRelativeToAssembly(exporterIFC, assemblyPlacement, elementHandles);
                        }

                        // We don't do this in RegisterAssemblyElement because we want to make sure that the IfcElementAssembly has been created.
                        ExporterCacheManager.ElementsInAssembliesCache.UnionWith(elementHandles);
                    }                  
                }

                // relate group elements to groups
                foreach (KeyValuePair<ElementId, GroupInfo> groupEntry in ExporterCacheManager.GroupCache)
                {
                    GroupInfo groupInfo = groupEntry.Value;
                    if (groupInfo == null)
                        continue;

                    if (groupInfo.GroupHandle != null && groupInfo.ElementHandles != null &&
                        groupInfo.ElementHandles.Count != 0)
                    {
                        Element group = document.GetElement(groupEntry.Key);
                        string guid = GUIDUtil.CreateSubElementGUID(group, (int)IFCGroupSubElements.RelAssignsToGroup);

                        IFCAnyHandle groupHandle = groupInfo.GroupHandle;
                        HashSet<IFCAnyHandle> elementHandles = groupInfo.ElementHandles;
                        if (elementHandles != null && groupHandle != null && elementHandles.Contains(groupHandle))
                            elementHandles.Remove(groupHandle);

                        if (elementHandles != null && groupHandle != null && elementHandles.Count > 0)
                        {
                            IFCInstanceExporter.CreateRelAssignsToGroup(file, guid, ownerHistory, null, null, elementHandles, null, groupHandle);
                        }
                    }
                }

                // create spatial structure holder
                ICollection<IFCAnyHandle> relatedElements = exporterIFC.GetRelatedElements();
                if (relatedElements.Count > 0)
                {
                    HashSet<IFCAnyHandle> relatedElementSet = new HashSet<IFCAnyHandle>(relatedElements);
                    string guid = GUIDUtil.CreateSubElementGUID(projectInfo, (int)IFCBuildingSubElements.RelContainedInSpatialStructure);
                    IFCInstanceExporter.CreateRelContainedInSpatialStructure(file, guid,
                        ownerHistory, null, null, relatedElementSet, buildingHnd);
                }

                ICollection<IFCAnyHandle> relatedProducts = exporterIFC.GetRelatedProducts();
                if (relatedProducts.Count > 0)
                {
                    string guid = GUIDUtil.CreateSubElementGUID(projectInfo, (int)IFCBuildingSubElements.RelAggregatesProducts);
                    ExporterCacheManager.ContainmentCache.SetGUIDForRelation(buildingHnd, guid);
                    ExporterCacheManager.ContainmentCache.AddRelations(buildingHnd, relatedProducts);
                }

                // create a default site if we have latitude and longitude information.
                if (IFCAnyHandleUtil.IsNullOrHasNoValue(exporterIFC.GetSite()))
                {
                    using (ProductWrapper productWrapper = ProductWrapper.Create(exporterIFC, true))
                    {
                        SiteExporter.ExportDefaultSite(exporterIFC, document, productWrapper);
                    }
                }

                IFCAnyHandle siteHandle = exporterIFC.GetSite();
                if (!IFCAnyHandleUtil.IsNullOrHasNoValue(siteHandle))
                {
                    ExporterCacheManager.ContainmentCache.AddRelation(exporterIFC.GetProject(), siteHandle);

                    // assoc. site to the building.
                    ExporterCacheManager.ContainmentCache.AddRelation(siteHandle, buildingHnd);

                    ExporterUtil.UpdateBuildingPlacement(buildingHnd, siteHandle);
                }
                else
                {
                    // relate building and project if no site
                    ExporterCacheManager.ContainmentCache.AddRelation(exporterIFC.GetProject(), buildingHnd);
                }

                // relate levels and products.
                RelateLevels(exporterIFC, document);

                // relate objects in containment cache.
                foreach (KeyValuePair<IFCAnyHandle, ICollection<IFCAnyHandle>> container in ExporterCacheManager.ContainmentCache)
                {
                    if (container.Value.Count() > 0)
                    {
                        string relationGUID = ExporterCacheManager.ContainmentCache.GetGUIDForRelation(container.Key);
                        ExporterUtil.RelateObjects(exporterIFC, relationGUID, container.Key, container.Value);
                    }
                }

                // These elements are created internally, but we allow custom property sets for them.  Create them here.
                using (ProductWrapper productWrapper = ProductWrapper.Create(exporterIFC, true))
                {
                    productWrapper.AddBuilding(projectInfo, buildingHnd);
                    if (projectInfo != null)
                        ExporterUtil.ExportRelatedProperties(exporterIFC, projectInfo, productWrapper);
                }

                // create material layer associations
                foreach (IFCAnyHandle materialSetLayerUsageHnd in ExporterCacheManager.MaterialLayerRelationsCache.Keys)
                {
                    IFCInstanceExporter.CreateRelAssociatesMaterial(file, GUIDUtil.CreateGUID(), ownerHistory,
                        null, null, ExporterCacheManager.MaterialLayerRelationsCache[materialSetLayerUsageHnd],
                        materialSetLayerUsageHnd);
                }

                // create material associations
                foreach (IFCAnyHandle materialHnd in ExporterCacheManager.MaterialRelationsCache.Keys)
                {
                    IFCInstanceExporter.CreateRelAssociatesMaterial(file, GUIDUtil.CreateGUID(), ownerHistory,
                        null, null, ExporterCacheManager.MaterialRelationsCache[materialHnd], materialHnd);
                }

                // create type relations
                foreach (IFCAnyHandle typeObj in ExporterCacheManager.TypeRelationsCache.Keys)
                {
                    IFCInstanceExporter.CreateRelDefinesByType(file, GUIDUtil.CreateGUID(), ownerHistory,
                        null, null, ExporterCacheManager.TypeRelationsCache[typeObj], typeObj);
                }

                // create type property relations
                foreach (TypePropertyInfo typePropertyInfo in ExporterCacheManager.TypePropertyInfoCache.Values)
                {
                    if (typePropertyInfo.AssignedToType)
                        continue;

                    ICollection<IFCAnyHandle> propertySets = typePropertyInfo.PropertySets;
                    ISet<IFCAnyHandle> elements = typePropertyInfo.Elements;

                    if (elements.Count == 0)
                        continue;

                    foreach (IFCAnyHandle propertySet in propertySets)
                    {
                        try
                        {
                            IFCInstanceExporter.CreateRelDefinesByProperties(file, GUIDUtil.CreateGUID(), ownerHistory,
                                null, null, elements, propertySet);
                        }
                        catch
                        {
                        }
                    }
                }

                // create space boundaries
                foreach (SpaceBoundary boundary in ExporterCacheManager.SpaceBoundaryCache)
                {
                    SpatialElementExporter.ProcessIFCSpaceBoundary(exporterIFC, boundary, file);
                }

                // create wall/wall connectivity objects
                if (ExporterCacheManager.WallConnectionDataCache.Count > 0)
                {
                    IList<IDictionary<ElementId, IFCAnyHandle>> hostObjects = exporterIFC.GetHostObjects();
                    List<int> relatingPriorities = new List<int>();
                    List<int> relatedPriorities = new List<int>();

                    foreach (WallConnectionData wallConnectionData in ExporterCacheManager.WallConnectionDataCache)
                    {
                        foreach (IDictionary<ElementId, IFCAnyHandle> mapForLevel in hostObjects)
                        {
                            IFCAnyHandle wallElementHandle, otherElementHandle;
                            if (!mapForLevel.TryGetValue(wallConnectionData.FirstId, out wallElementHandle))
                                continue;
                            if (!mapForLevel.TryGetValue(wallConnectionData.SecondId, out otherElementHandle))
                                continue;

                            // NOTE: Definition of RelConnectsPathElements has the connection information reversed
                            // with respect to the order of the paths.
                            IFCInstanceExporter.CreateRelConnectsPathElements(file, GUIDUtil.CreateGUID(), ownerHistory,
                                null, null, wallConnectionData.ConnectionGeometry, wallElementHandle, otherElementHandle, relatingPriorities,
                                relatedPriorities, wallConnectionData.SecondConnectionType, wallConnectionData.FirstConnectionType);
                        }
                    }
                }

                // create Zones
                {
                    string relAssignsToGroupName = "Spatial Zone Assignment";
                    foreach (string zoneName in ExporterCacheManager.ZoneInfoCache.Keys)
                    {
                        ZoneInfo zoneInfo = ExporterCacheManager.ZoneInfoCache.Find(zoneName);
                        if (zoneInfo != null)
                        {
                            IFCAnyHandle zoneHandle = IFCInstanceExporter.CreateZone(file, GUIDUtil.CreateGUID(), ownerHistory,
                                zoneName, zoneInfo.Description, zoneInfo.ObjectType);
                            IFCInstanceExporter.CreateRelAssignsToGroup(file, GUIDUtil.CreateGUID(), ownerHistory,
                                relAssignsToGroupName, null, zoneInfo.RoomHandles, null, zoneHandle);

                            HashSet<IFCAnyHandle> zoneHnds = new HashSet<IFCAnyHandle>();
                            zoneHnds.Add(zoneHandle);

                            foreach (KeyValuePair<string, IFCAnyHandle> classificationReference in zoneInfo.ClassificationReferences)
                            {
                                IFCAnyHandle relAssociates = IFCInstanceExporter.CreateRelAssociatesClassification(file, GUIDUtil.CreateGUID(),
                                    ownerHistory, classificationReference.Key, "", zoneHnds, classificationReference.Value);
                            }

                            if (!IFCAnyHandleUtil.IsNullOrHasNoValue(zoneInfo.EnergyAnalysisProperySetHandle))
                            {
                                IFCInstanceExporter.CreateRelDefinesByProperties(file, GUIDUtil.CreateGUID(),
                                    ownerHistory, null, null, zoneHnds, zoneInfo.EnergyAnalysisProperySetHandle);
                            }

                            if (!IFCAnyHandleUtil.IsNullOrHasNoValue(zoneInfo.ZoneCommonProperySetHandle))
                            {
                                IFCInstanceExporter.CreateRelDefinesByProperties(file, GUIDUtil.CreateGUID(),
                                    ownerHistory, null, null, zoneHnds, zoneInfo.ZoneCommonProperySetHandle);
                            }
                        }
                    }
                }

                // create Space Occupants
                {
                    foreach (string spaceOccupantName in ExporterCacheManager.SpaceOccupantInfoCache.Keys)
                    {
                        SpaceOccupantInfo spaceOccupantInfo = ExporterCacheManager.SpaceOccupantInfoCache.Find(spaceOccupantName);
                        if (spaceOccupantInfo != null)
                        {
                            IFCAnyHandle person = IFCInstanceExporter.CreatePerson(file, null, spaceOccupantName, null, null, null, null, null, null);
                            IFCAnyHandle spaceOccupantHandle = IFCInstanceExporter.CreateOccupant(file, GUIDUtil.CreateGUID(),
                                ownerHistory, null, null, spaceOccupantName, person, IFCOccupantType.NotDefined);
                            IFCInstanceExporter.CreateRelOccupiesSpaces(file, GUIDUtil.CreateGUID(), ownerHistory,
                                null, null, spaceOccupantInfo.RoomHandles, null, spaceOccupantHandle, null);

                            HashSet<IFCAnyHandle> spaceOccupantHandles = new HashSet<IFCAnyHandle>();
                            spaceOccupantHandles.Add(spaceOccupantHandle);

                            foreach (KeyValuePair<string, IFCAnyHandle> classificationReference in spaceOccupantInfo.ClassificationReferences)
                            {
                                IFCAnyHandle relAssociates = IFCInstanceExporter.CreateRelAssociatesClassification(file, GUIDUtil.CreateGUID(),
                                    ownerHistory, classificationReference.Key, "", spaceOccupantHandles, classificationReference.Value);
                            }

                            if (spaceOccupantInfo.SpaceOccupantProperySetHandle != null && spaceOccupantInfo.SpaceOccupantProperySetHandle.HasValue)
                            {
                                IFCAnyHandle relHnd = IFCInstanceExporter.CreateRelDefinesByProperties(file, GUIDUtil.CreateGUID(),
                                    ownerHistory, null, null, spaceOccupantHandles, spaceOccupantInfo.SpaceOccupantProperySetHandle);
                            }
                        }
                    }
                }

                // Create systems.
                HashSet<IFCAnyHandle> relatedBuildings = new HashSet<IFCAnyHandle>();
                relatedBuildings.Add(buildingHnd);

                using (ProductWrapper productWrapper = ProductWrapper.Create(exporterIFC, true))
                {
                    foreach (KeyValuePair<ElementId, ISet<IFCAnyHandle>> system in ExporterCacheManager.SystemsCache.BuiltInSystemsCache)
                    {
                        MEPSystem systemElem = document.GetElement(system.Key) as MEPSystem;
                        if (systemElem == null)
                            continue;

                        Element baseEquipment = systemElem.BaseEquipment;
                        if (baseEquipment != null)
                        {
                            IFCAnyHandle memberHandle = ExporterCacheManager.MEPCache.Find(baseEquipment.Id);
                            if (!IFCAnyHandleUtil.IsNullOrHasNoValue(memberHandle))
                                system.Value.Add(memberHandle);
                        }

                        ElementType systemElemType = document.GetElement(systemElem.GetTypeId()) as ElementType;
                        string name = NamingUtil.GetNameOverride(systemElem, systemElem.Name);
                        string desc = NamingUtil.GetDescriptionOverride(systemElem, null);
                        string objectType = NamingUtil.GetObjectTypeOverride(systemElem,
                            (systemElemType != null) ? systemElemType.Name : "");

                        string systemGUID = GUIDUtil.CreateGUID(systemElem);
                        IFCAnyHandle systemHandle = IFCInstanceExporter.CreateSystem(file, systemGUID,
                            ownerHistory, name, desc, objectType);
                        
                        productWrapper.AddSystem(systemElem, systemHandle);

                        IFCAnyHandle relServicesBuildings = IFCInstanceExporter.CreateRelServicesBuildings(file, GUIDUtil.CreateGUID(),
                            ownerHistory, null, null, systemHandle, relatedBuildings);

                        IFCObjectType? objType = null;
                        if (!ExporterCacheManager.ExportOptionsCache.ExportAsCoordinationView2)
                            objType = IFCObjectType.Product;
                        IFCAnyHandle relAssignsToGroup = IFCInstanceExporter.CreateRelAssignsToGroup(file, GUIDUtil.CreateGUID(),
                            ownerHistory, null, null, system.Value, objType, systemHandle);
                    }
                }

                using (ProductWrapper productWrapper = ProductWrapper.Create(exporterIFC, true))
                {
                    foreach (KeyValuePair<ElementId, ISet<IFCAnyHandle>> entries in ExporterCacheManager.SystemsCache.ElectricalSystemsCache)
                    {
                        ElementId systemId = entries.Key;
                        MEPSystem systemElem = document.GetElement(systemId) as MEPSystem;
                        if (systemElem == null)
                            continue;

                        Element baseEquipment = systemElem.BaseEquipment;
                        if (baseEquipment != null)
                        {
                            IFCAnyHandle memberHandle = ExporterCacheManager.MEPCache.Find(baseEquipment.Id);
                            if (!IFCAnyHandleUtil.IsNullOrHasNoValue(memberHandle))
                                entries.Value.Add(memberHandle);
                        }

                        // The Elements property below can throw an InvalidOperationException in some cases, which could
                        // crash the export.  Protect against this without having too generic a try/catch block.
                        try
                        {
                            ElementSet members = systemElem.Elements;
                            foreach (Element member in members)
                            {
                                IFCAnyHandle memberHandle = ExporterCacheManager.MEPCache.Find(member.Id);
                                if (!IFCAnyHandleUtil.IsNullOrHasNoValue(memberHandle))
                                    entries.Value.Add(memberHandle);
                            }
                        }
                        catch
                        {
                        }

                        if (entries.Value.Count == 0)
                            continue;

                        ElementType systemElemType = document.GetElement(systemElem.GetTypeId()) as ElementType;
                        string name = NamingUtil.GetNameOverride(systemElem, systemElem.Name);
                        string desc = NamingUtil.GetDescriptionOverride(systemElem, null);
                        string objectType = NamingUtil.GetObjectTypeOverride(systemElem,
                            (systemElemType != null) ? systemElemType.Name : "");

                        string systemGUID = GUIDUtil.CreateGUID(systemElem);
                        IFCAnyHandle systemHandle = IFCInstanceExporter.CreateSystem(file,
                            systemGUID, ownerHistory, name, desc, objectType);

                        productWrapper.AddSystem(systemElem, systemHandle);

                        IFCAnyHandle relServicesBuildings = IFCInstanceExporter.CreateRelServicesBuildings(file, GUIDUtil.CreateGUID(),
                            ownerHistory, null, null, systemHandle, relatedBuildings);

                        IFCObjectType? objType = null;
                        if (!ExporterCacheManager.ExportOptionsCache.ExportAsCoordinationView2)
                            objType = IFCObjectType.Product;
                        IFCAnyHandle relAssignsToGroup = IFCInstanceExporter.CreateRelAssignsToGroup(file, GUIDUtil.CreateGUID(),
                            ownerHistory, null, null, entries.Value, objType, systemHandle);
                    }
                }

                // Add presentation layer assignments - this is in addition to those added in EndExportInternal, and will
                // eventually replace the internal routine.
                foreach (KeyValuePair<string, ICollection<IFCAnyHandle>> presentationLayerSet in ExporterCacheManager.PresentationLayerSetCache)
                {
                    ISet<IFCAnyHandle> validHandles = new HashSet<IFCAnyHandle>();
                    foreach (IFCAnyHandle handle in presentationLayerSet.Value)
                    {
                        if (!IFCAnyHandleUtil.IsNullOrHasNoValue(handle))
                            validHandles.Add(handle);
                    }

                    if (validHandles.Count > 0)
                        IFCInstanceExporter.CreatePresentationLayerAssignment(file, presentationLayerSet.Key, null, validHandles, null);
                }

                // Add door/window openings.
                ExporterCacheManager.DoorWindowDelayedOpeningCreatorCache.ExecuteCreators(exporterIFC, document);

                foreach (SpaceInfo spaceInfo in ExporterCacheManager.SpaceInfoCache.SpaceInfos.Values)
                {
                    if (spaceInfo.RelatedElements.Count > 0)
                    {
                        IFCInstanceExporter.CreateRelContainedInSpatialStructure(file, GUIDUtil.CreateGUID(), ownerHistory,
                            null, null, spaceInfo.RelatedElements, spaceInfo.SpaceHandle);
                    }
                }

                // Potentially modify elements with GUID values.
                if (ExporterCacheManager.GUIDsToStoreCache.Count > 0)
                {
                    using (SubTransaction st = new SubTransaction(document))
                    {
                        st.Start();
                        foreach (KeyValuePair<KeyValuePair<Element, BuiltInParameter>, string> elementAndGUID in ExporterCacheManager.GUIDsToStoreCache)
                        {
                            if (elementAndGUID.Key.Key == null || elementAndGUID.Key.Value == BuiltInParameter.INVALID || elementAndGUID.Value == null)
                                continue;

                            ParameterUtil.SetStringParameter(elementAndGUID.Key.Key, elementAndGUID.Key.Value, elementAndGUID.Value);
                        }
                        st.Commit();
                    }
                }

                // Allow native code to remove some unused handles, assign presentation map information and clear internal caches.
                ExporterIFCUtils.EndExportInternal(exporterIFC);

                //create header

                ExportOptionsCache exportOptionsCache = ExporterCacheManager.ExportOptionsCache;

                string coordinationView = null;
                if (exportOptionsCache.ExportAsCoordinationView2)
                    coordinationView = "CoordinationView_V2.0";
                else
                    coordinationView = "CoordinationView";

                List<string> descriptions = new List<string>();
                if (ExporterCacheManager.ExportOptionsCache.ExportAs2x2 || ExporterUtil.DoCodeChecking(exportOptionsCache))
                {
                    descriptions.Add("IFC2X_PLATFORM");
                }
                else
                {
                    string currentLine;
                    if (String.Compare(exportOptionsCache.SelectedConfigName, "FMHandOverView") == 0)
                    {
                        currentLine = string.Format("ViewDefinition [{0}{1}{2}{3}]",
                           coordinationView,
                           exportOptionsCache.ExportBaseQuantities ? ", QuantityTakeOffAddOnView" : "",
                           ", ", exportOptionsCache.SelectedConfigName);
                    }
                    else
                    {
                        currentLine = string.Format("ViewDefinition [{0}{1}]",
                           coordinationView,
                           exportOptionsCache.ExportBaseQuantities ? ", QuantityTakeOffAddOnView" : "");
                    }

                    descriptions.Add(currentLine);
                  
                }

                string projectNumber = (projectInfo != null) ? projectInfo.Number : null;
                string projectName = (projectInfo != null) ? projectInfo.Name : null;
                string projectStatus = (projectInfo != null) ? projectInfo.Status : null;

                if (projectNumber == null)
                    projectNumber = string.Empty;
                if (projectName == null)
                    projectName = exportOptionsCache.FileName;
                if (projectStatus == null)
                    projectStatus = string.Empty;

                IFCAnyHandle project = exporterIFC.GetProject();
                if (!IFCAnyHandleUtil.IsNullOrHasNoValue(project))
                    IFCAnyHandleUtil.UpdateProject(project, projectNumber, projectName, projectStatus);

                IFCInstanceExporter.CreateFileSchema(file);
                IFCInstanceExporter.CreateFileDescription(file, descriptions);
                // Get stored File Header information from the UI and use it for export
                IFCFileHeader fHeader = new IFCFileHeader();
                IFCFileHeaderItem fHItem = null;

                fHeader.GetSavedFileHeader(document, out fHItem);

                List<string> author = new List<string>();
                if (String.IsNullOrEmpty(fHItem.AuthorName) == false)
                {
                    author.Add(fHItem.AuthorName);
                    if (String.IsNullOrEmpty(fHItem.AuthorEmail) == false)
                        author.Add(fHItem.AuthorEmail);
                }
                else
                    author.Add(String.Empty);

                List<string> organization = new List<string>();
                if (String.IsNullOrEmpty(fHItem.Organization) == false)
                    organization.Add(fHItem.Organization);
                else
                    organization.Add(String.Empty);

                string versionInfos = document.Application.VersionBuild + " - " + ExporterCacheManager.ExportOptionsCache.ExporterVersion + " - " + ExporterCacheManager.ExportOptionsCache.ExporterUIVersion;

                if (fHItem.Authorization == null)
                    fHItem.Authorization = String.Empty;

                IFCInstanceExporter.CreateFileName(file, projectNumber, author, organization, document.Application.VersionName,
                    versionInfos, fHItem.Authorization);

                transaction.Commit();

                IFCFileWriteOptions writeOptions = new IFCFileWriteOptions();
                writeOptions.FileName = exportOptionsCache.FileName;
                writeOptions.FileFormat = exportOptionsCache.IFCFileFormat;
                if (writeOptions.FileFormat == IFCFileFormat.IfcXML || writeOptions.FileFormat == IFCFileFormat.IfcXMLZIP)
                {
                    writeOptions.XMLConfigFileName = Path.Combine(ExporterUtil.RevitProgramPath, "EDM\\ifcXMLconfiguration.xml");
                }
                file.Write(writeOptions);
            }
        }

        private string GetLanguageExtension(LanguageType langType)
        {
            switch (langType)
            {
                case LanguageType.English_USA:
                    return " (ENU)";
                case LanguageType.German:
                    return " (DEU)";
                case LanguageType.Spanish:
                    return " (ESP)";
                case LanguageType.French:
                    return " (FRA)";
                case LanguageType.Italian:
                    return " (ITA)";
                case LanguageType.Dutch:
                    return " (NLD)";
                case LanguageType.Chinese_Simplified:
                    return " (CHS)";
                case LanguageType.Chinese_Traditional:
                    return " (CHT)";
                case LanguageType.Japanese:
                    return " (JPN)";
                case LanguageType.Korean:
                    return " (KOR)";
                case LanguageType.Russian:
                    return " (RUS)";
                case LanguageType.Czech:
                    return " (CSY)";
                case LanguageType.Polish:
                    return " (PLK)";
                case LanguageType.Hungarian:
                    return " (HUN)";
                case LanguageType.Brazilian_Portuguese:
                    return " (PTB)";
                default:
                    return "";
            }
        }

        private long GetCreationDate(Document document)
        {
            string pathName = document.PathName;
            if (!String.IsNullOrEmpty(pathName))
            {
                if (document.IsWorkshared && ModelPathUtils.IsValidUserVisibleFullServerPath(pathName))
                {
                   //This is just a temporary fix for SPR#226541, currently it's unable to get the FileInfo of a server based file stored at server.
                   //Should server based file stored at server support this functionality and how to support will be tracked by SPR#226761. 
                   return 0;
                }
                FileInfo fileInfo = new FileInfo(pathName);
                DateTime creationTimeUtc = fileInfo.CreationTimeUtc;
                // The IfcTimeStamp is measured in seconds since 1/1/1970.  As such, we divide by 10,000,000 (100-ns ticks in a second)
                // and subtract the 1/1/1970 offset.
                return creationTimeUtc.ToFileTimeUtc() / 10000000 - 11644473600;
            }
            return 0;
        }

        /// <summary>
        /// Creates the application information.
        /// </summary>
        /// <param name="file">The IFC file.</param>
        /// <param name="app">The application object.</param>
        /// <returns>The handle of IFC file.</returns>
        private IFCAnyHandle CreateApplicationInformation(IFCFile file, Document document)
        {
            Application app = document.Application;
            string pathName = document.PathName;
            LanguageType langType = ExporterCacheManager.LanguageType;
            string languageExtension = GetLanguageExtension(langType);
            string productFullName = app.VersionName + languageExtension;
            string productVersion = app.VersionNumber;
            string productIdentifier = "Revit";

            IFCAnyHandle developer = IFCInstanceExporter.CreateOrganization(file, null, productFullName, null, null, null);
            IFCAnyHandle application = IFCInstanceExporter.CreateApplication(file, developer, productVersion, productFullName, productIdentifier);
            return application;
        }

        /// <summary>
        /// Creates the 3D and 2D contexts information.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="doc">The document provides the ProjectLocation.</param>
        /// <returns>The collection contains the 3D/2D context (not sub-context) handles of IFC file.</returns>
        private HashSet<IFCAnyHandle> CreateContextInformation(ExporterIFC exporterIFC, Document doc)
        {
            HashSet<IFCAnyHandle> repContexts = new HashSet<IFCAnyHandle>();
            double scaledPrecision = UnitUtil.ScaleLength(doc.Application.VertexTolerance/10.0);
            int exponent = Convert.ToInt32(Math.Log10(scaledPrecision));
            double precision = Math.Pow(10.0, exponent);
            
            IFCFile file = exporterIFC.GetFile();
            IFCAnyHandle origin = ExporterIFCUtils.GetGlobal3DOriginHandle();
            IFCAnyHandle wcs = IFCInstanceExporter.CreateAxis2Placement3D(file, origin, null, null);

            ProjectLocation projLoc = doc.ActiveProjectLocation;
            double trueNorthAngleInRadians = 0.0;
            try
            {
                ProjectPosition projPos = projLoc.get_ProjectPosition(XYZ.Zero);
                trueNorthAngleInRadians = projPos.Angle;
            }
            catch (InternalException)
            {
                //fail to get true north, ignore
            }

            // CoordinationView2.0 requires that we always export true north, even if it is the same as project north.
            IFCAnyHandle trueNorth = null;
            {
                double trueNorthAngleConverted = -trueNorthAngleInRadians + Math.PI / 2.0;
                List<double> dirRatios = new List<double>();
                dirRatios.Add(Math.Cos(trueNorthAngleConverted));
                dirRatios.Add(Math.Sin(trueNorthAngleConverted));
                trueNorth = IFCInstanceExporter.CreateDirection(file, dirRatios);
            }

            int dimCount = 3;
            IFCAnyHandle context3D = IFCInstanceExporter.CreateGeometricRepresentationContext(file, null,
                "Model", dimCount, precision, wcs, trueNorth);
            // CoordinationView2.0 requires sub-contexts of "Axis", "Body", and "Box".  We will use these for regular export also.
            {
                IFCAnyHandle context3DAxis = IFCInstanceExporter.CreateGeometricRepresentationSubContext(file,
                    "Axis", "Model", context3D, null, Toolkit.IFCGeometricProjection.Graph_View, null);
                IFCAnyHandle context3DBody = IFCInstanceExporter.CreateGeometricRepresentationSubContext(file,
                    "Body", "Model", context3D, null, Toolkit.IFCGeometricProjection.Model_View, null);
                IFCAnyHandle context3DBox = IFCInstanceExporter.CreateGeometricRepresentationSubContext(file,
                    "Box", "Model", context3D, null, Toolkit.IFCGeometricProjection.Model_View, null);
                IFCAnyHandle context3DFootPrint = IFCInstanceExporter.CreateGeometricRepresentationSubContext(file,
                    "FootPrint", "Model", context3D, null, Toolkit.IFCGeometricProjection.Model_View, null);

                exporterIFC.Set3DContextHandle(context3DAxis, "Axis");
                exporterIFC.Set3DContextHandle(context3DBody, "Body");
                exporterIFC.Set3DContextHandle(context3DBox, "Box");
                exporterIFC.Set3DContextHandle(context3DFootPrint, "FootPrint");
            }

            exporterIFC.Set3DContextHandle(context3D, "");
            repContexts.Add(context3D); // Only Contexts in list, not sub-contexts.

            if (ExporterCacheManager.ExportOptionsCache.ExportAnnotations)
            {
                string context2DType = "Annotation";
                IFCAnyHandle context2DHandle = IFCInstanceExporter.CreateGeometricRepresentationContext(file,
                    null, context2DType, dimCount, precision, wcs, trueNorth);

                IFCAnyHandle context2D = IFCInstanceExporter.CreateGeometricRepresentationSubContext(file,
                    null, context2DType, context2DHandle, 0.01, Toolkit.IFCGeometricProjection.Plan_View, null);

                exporterIFC.Set2DContextHandle(context2D);
                repContexts.Add(context2DHandle); // Only Contexts in list, not sub-contexts.
            }

            return repContexts;
        }

        /// <summary>
        /// Creates the IfcProject.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="doc">The document provides the owner information.</param>
        /// <param name="application">The handle of IFC file to create the owner history.</param>
        private void CreateProject(ExporterIFC exporterIFC, Document doc, IFCAnyHandle application)
        {
            string familyName;
            string givenName;
            List<string> middleNames;
            List<string> prefixTitles;
            List<string> suffixTitles;

            string author = String.Empty;
            ProjectInfo projectInfo = doc.ProjectInformation;
            if (projectInfo != null)
            {
                try
                {
                    author = projectInfo.Author;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                    //if failed to get author from project info, try to get the username from application later.
                }
            }

            if (String.IsNullOrEmpty(author))
            {
                author = doc.Application.Username;
            }

            NamingUtil.ParseName(author, out familyName, out givenName, out middleNames, out prefixTitles, out suffixTitles);

            IFCFile file = exporterIFC.GetFile();

            IFCAnyHandle telecomAddress = GetTelecomAddressFromExtStorage(file, doc);
            IList<IFCAnyHandle> telecomAddresses = null;
            if (telecomAddress != null)
            {
                telecomAddresses = new List<IFCAnyHandle>();
                telecomAddresses.Add(telecomAddress);
            }

            IFCAnyHandle person = IFCInstanceExporter.CreatePerson(file, null, familyName, givenName, middleNames,
                prefixTitles, suffixTitles, null, telecomAddresses);

            string organizationName = String.Empty;
            string organizationDescription = String.Empty;
            if (projectInfo != null)
            {
                try
                {
                    organizationName = projectInfo.OrganizationName;
                    organizationDescription = projectInfo.OrganizationDescription;
                }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException)
                {
                }
            }

            int creationDate = (int) GetCreationDate(doc);
            
            IFCAnyHandle organization = IFCInstanceExporter.CreateOrganization(file, null, organizationName, organizationDescription,
                null, null);

            IFCAnyHandle owningUser = IFCInstanceExporter.CreatePersonAndOrganization(file, person, organization, null);
            IFCAnyHandle ownerHistory = IFCInstanceExporter.CreateOwnerHistory(file, owningUser, application, null,
                Toolkit.IFCChangeAction.NoChange, null, null, null, creationDate);

            exporterIFC.SetOwnerHistoryHandle(ownerHistory);

            IFCAnyHandle units = CreateDefaultUnits(exporterIFC, doc);
            HashSet<IFCAnyHandle> repContexts = CreateContextInformation(exporterIFC, doc);

            // As per IFC implementer's agreement, we get IfcProject.Name from ProjectInfo.Number and IfcProject.Longname from ProjectInfo.Name 
            string projectName = (projectInfo != null) ? projectInfo.Number : null;
            string projectLongName = (projectInfo != null) ? projectInfo.Name :null;

            // Get project description if it is set in the Project info
            string projectObjectType = (projectInfo != null) ? NamingUtil.GetObjectTypeOverride(projectInfo, null) : null;
            string projectDescription = (projectInfo != null) ? NamingUtil.GetDescriptionOverride(projectInfo, null) : null;

            string projectPhase = null;
            if (projectInfo != null)
                ParameterUtil.GetStringValueFromElement(projectInfo.Id, "Project Phase", out projectPhase);

            string projectGUID = GUIDUtil.CreateProjectLevelGUID(doc, IFCProjectLevelGUIDType.Project);
            IFCAnyHandle projectHandle = IFCInstanceExporter.CreateProject(file, projectGUID, ownerHistory,
                projectName, projectDescription, projectObjectType, projectLongName, projectPhase, repContexts, units);
            exporterIFC.SetProject(projectHandle);

            if (ExporterCacheManager.ExportOptionsCache.FileVersion == IFCVersion.IFCCOBIE)
            {
                HashSet<IFCAnyHandle> projectHandles = new HashSet<IFCAnyHandle>();
                projectHandles.Add(projectHandle);
                string clientName = (projectInfo != null) ? projectInfo.ClientName : String.Empty;
                IFCAnyHandle clientOrg = IFCInstanceExporter.CreateOrganization(file, null, clientName, null, null, null);
                IFCAnyHandle actor = IFCInstanceExporter.CreateActor(file, GUIDUtil.CreateGUID(), ownerHistory, null, null, null, clientOrg);
                IFCInstanceExporter.CreateRelAssignsToActor(file, GUIDUtil.CreateGUID(), ownerHistory, "Project Client/Owner", null, projectHandles, null, actor, null);

                IFCAnyHandle architectActor = IFCInstanceExporter.CreateActor(file, GUIDUtil.CreateGUID(), ownerHistory, null, null, null, person);
                IFCInstanceExporter.CreateRelAssignsToActor(file, GUIDUtil.CreateGUID(), ownerHistory, "Project Architect", null, projectHandles, null, architectActor, null);
            }
        }

        private IFCAnyHandle GetTelecomAddressFromExtStorage(IFCFile file, Document document)
        {
            IFCFileHeader fHeader = new IFCFileHeader();
            IFCFileHeaderItem fHItem = null;

            fHeader.GetSavedFileHeader(document, out fHItem);
            if (!String.IsNullOrEmpty(fHItem.AuthorEmail))
            {
                IList<string> electronicMailAddress = new List<string>();
                electronicMailAddress.Add(fHItem.AuthorEmail);
                return IFCInstanceExporter.CreateTelecomAddress(file, null, null, null, null, null, null, electronicMailAddress, null);
            }

            return null;
        }
        
        /// <summary>
        /// Create IFC Address from the saved data obtained by the UI and saved in the extensible storage
        /// </summary>
        /// <param name="file"></param>
        /// <param name="document"></param>
        /// <returns>The handle of IFC file.</returns>
        private IFCAnyHandle CreateIFCAddressFromExtStorage(IFCFile file, Document document)
        {
            IFCAddress savedAddress = new IFCAddress();
            IFCAddressItem savedAddressItem;

            if (savedAddress.GetSavedAddress(document, out savedAddressItem) == true)
            {
                IFCAnyHandle postalAddress;
                
                // We have address saved in the extensible storage
                List<string> addressLines = new List<string>();
                if (!String.IsNullOrEmpty(savedAddressItem.AddressLine1))
                {
                    addressLines.Add(savedAddressItem.AddressLine1);
                    if (!String.IsNullOrEmpty(savedAddressItem.AddressLine2))
                        addressLines.Add(savedAddressItem.AddressLine2);
                }

                IFCAddressType? addressPurpose = null;
                if (!String.IsNullOrEmpty(savedAddressItem.Purpose))
                {
                    addressPurpose = IFCAddressType.UserDefined;     // set this as default value
                    if (String.Compare(savedAddressItem.Purpose, "OFFICE", true) == 0)
                        addressPurpose = Toolkit.IFCAddressType.Office;
                    else if (String.Compare(savedAddressItem.Purpose, "SITE", true) == 0)
                        addressPurpose = Toolkit.IFCAddressType.Site;
                    else if (String.Compare(savedAddressItem.Purpose, "HOME", true) == 0)
                        addressPurpose = Toolkit.IFCAddressType.Home;
                    else if (String.Compare(savedAddressItem.Purpose, "DISTRIBUTIONPOINT", true) == 0)
                        addressPurpose = Toolkit.IFCAddressType.DistributionPoint;
                    else if (String.Compare(savedAddressItem.Purpose, "USERDEFINED", true) == 0)
                        addressPurpose = Toolkit.IFCAddressType.UserDefined;
                }

                postalAddress = IFCInstanceExporter.CreatePostalAddress(file, addressPurpose, savedAddressItem.Description, savedAddressItem.UserDefinedPurpose,
                   savedAddressItem.InternalLocation, addressLines, savedAddressItem.POBox, savedAddressItem.TownOrCity, savedAddressItem.RegionOrState, savedAddressItem.PostalCode, 
                   savedAddressItem.Country);

                return postalAddress;
            }

            return null;
        }

        /// <summary>
        /// Creates the IfcPostalAddress, and assigns it to the file.
        /// </summary>
        /// <param name="file">The IFC file.</param>
        /// <param name="address">The address string.</param>
        /// <param name="town">The town string.</param>
        /// <returns>The handle of IFC file.</returns>
        private IFCAnyHandle CreateIFCAddress(IFCFile file, Document document, ProjectInfo projInfo)
        {
            IFCAnyHandle postalAddress = null;
            postalAddress = CreateIFCAddressFromExtStorage(file, document);
            if (postalAddress != null)
                return postalAddress;

            string projectAddress = projInfo != null ? projInfo.Address : String.Empty;
            SiteLocation siteLoc = document.ActiveProjectLocation.SiteLocation;
            string location = siteLoc != null ? siteLoc.PlaceName : String.Empty;

            if (projectAddress == null)
                projectAddress = String.Empty;
            if (location == null)
                location = String.Empty;

            List<string> parsedAddress = new List<string>();
            string city = String.Empty;
            string state = String.Empty;
            string postCode = String.Empty;
            string country = String.Empty;

            string parsedTown = location;
            int commaLoc = -1;
            do
            {
                commaLoc = parsedTown.IndexOf(',');
                if (commaLoc >= 0)
                {
                    if (commaLoc > 0)
                        parsedAddress.Add(parsedTown.Substring(0, commaLoc));
                    parsedTown = parsedTown.Substring(commaLoc + 1).TrimStart(' ');
                }
                else if (!String.IsNullOrEmpty(parsedTown))
                    parsedAddress.Add(parsedTown);
            } while (commaLoc >= 0);

            int numLines = parsedAddress.Count;
            if (numLines > 0)
            {
                country = parsedAddress[numLines - 1];
                numLines--;
            }

            if (numLines > 0)
            {
                int spaceLoc = parsedAddress[numLines - 1].IndexOf(' ');
                if (spaceLoc > 0)
                {
                    state = parsedAddress[numLines - 1].Substring(0, spaceLoc);
                    postCode = parsedAddress[numLines - 1].Substring(spaceLoc + 1);
                }
                else
                    state = parsedAddress[numLines - 1];
                numLines--;
            }

            if (numLines > 0)
            {
                city = parsedAddress[numLines - 1];
                numLines--;
            }

            List<string> addressLines = new List<string>();
            if (!String.IsNullOrEmpty(projectAddress))
                addressLines.Add(projectAddress);

            for (int ii = 0; ii < numLines; ii++)
            {
                addressLines.Add(parsedAddress[ii]);
            }

            postalAddress = IFCInstanceExporter.CreatePostalAddress(file, null, null, null,
               null, addressLines, null, city, state, postCode, country);

            return postalAddress;
        }

        private IFCAnyHandle CreateSIUnit(IFCFile file, UnitType? unitType, IFCUnit ifcUnitType, IFCSIUnitName unitName, IFCSIPrefix? prefix, DisplayUnitType? dut)
        {
            IFCAnyHandle siUnit = IFCInstanceExporter.CreateSIUnit(file, ifcUnitType, prefix, unitName);
            if (unitType.HasValue && dut.HasValue)
            {
                double scaleFactor = UnitUtils.ConvertFromInternalUnits(1.0, dut.Value);
                ExporterCacheManager.UnitsCache.AddUnit(unitType.Value, siUnit, scaleFactor, 0.0);
            }

            return siUnit;
        }

        /// <summary>
        /// Creates the IfcUnitAssignment.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="doc">The document provides ProjectUnit and DisplayUnitSystem.</param>
        /// <returns>The IFC handle.</returns>
        private IFCAnyHandle CreateDefaultUnits(ExporterIFC exporterIFC, Document doc)
        {
            HashSet<IFCAnyHandle> unitSet = new HashSet<IFCAnyHandle>();
            IFCFile file = exporterIFC.GetFile();
            bool exportToCOBIE = ExporterCacheManager.ExportOptionsCache.FileVersion == IFCVersion.IFCCOBIE;

            IFCAnyHandle lenSIBaseUnit = null;
            {
                bool lenConversionBased = false;
                bool lenUseDefault = false;

                IFCUnit lenUnitType = IFCUnit.LengthUnit;
                IFCSIPrefix? lenPrefix = null;
                IFCSIUnitName lenUnitName = IFCSIUnitName.Metre;
                string lenConvName = null;

                FormatOptions lenFormatOptions = doc.GetUnits().GetFormatOptions(UnitType.UT_Length);
                switch (lenFormatOptions.DisplayUnits)
                {
                    case DisplayUnitType.DUT_METERS:
                    case DisplayUnitType.DUT_METERS_CENTIMETERS:
                        break;
                    case DisplayUnitType.DUT_CENTIMETERS:
                        lenPrefix = IFCSIPrefix.Centi;
                        break;
                    case DisplayUnitType.DUT_MILLIMETERS:
                        lenPrefix = IFCSIPrefix.Milli;
                        break;
                    case DisplayUnitType.DUT_DECIMAL_FEET:
                    case DisplayUnitType.DUT_FEET_FRACTIONAL_INCHES:
                        {
                            if (exportToCOBIE)
                                lenConvName = "foot";
                            else
                                lenConvName = "FOOT";
                            lenConversionBased = true;
                        }
                        break;
                    case DisplayUnitType.DUT_FRACTIONAL_INCHES:
                    case DisplayUnitType.DUT_DECIMAL_INCHES:
                        {
                            if (exportToCOBIE)
                                lenConvName = "inch";
                            else
                                lenConvName = "INCH";
                        }
                        lenConversionBased = true;
                        break;
                    default:
                        {
                            //Couldn't find display unit type conversion -- assuming foot
                            if (exportToCOBIE)
                                lenConvName = "foot";
                            else
                                lenConvName = "FOOT";
                            lenConversionBased = true;
                            lenUseDefault = true;
                        }
                        break;
                }

                double lengthScaleFactor = UnitUtils.ConvertFromInternalUnits(1.0, lenUseDefault ? DisplayUnitType.DUT_DECIMAL_FEET : lenFormatOptions.DisplayUnits);
                IFCAnyHandle lenSIUnit = IFCInstanceExporter.CreateSIUnit(file, lenUnitType, lenPrefix, lenUnitName);
                if (lenPrefix == null)
                    lenSIBaseUnit = lenSIUnit;
                else
                    lenSIBaseUnit = IFCInstanceExporter.CreateSIUnit(file, lenUnitType, null, lenUnitName);

                if (lenConversionBased)
                {
                    double lengthSIScaleFactor = UnitUtils.ConvertFromInternalUnits(1.0, DisplayUnitType.DUT_METERS) / lengthScaleFactor;
                    IFCAnyHandle lenDims = IFCInstanceExporter.CreateDimensionalExponents(file, 1, 0, 0, 0, 0, 0, 0); // length
                    IFCAnyHandle lenConvFactor = IFCInstanceExporter.CreateMeasureWithUnit(file, Toolkit.IFCDataUtil.CreateAsRatioMeasure(lengthSIScaleFactor),
                        lenSIUnit);
                    lenSIUnit = IFCInstanceExporter.CreateConversionBasedUnit(file, lenDims, lenUnitType, lenConvName, lenConvFactor);
                }

                unitSet.Add(lenSIUnit);      // created above, so unique.
                ExporterCacheManager.UnitsCache.AddUnit(UnitType.UT_Length, lenSIUnit, lengthScaleFactor, 0.0);
            }

            {
                bool areaConversionBased = false;
                bool areaUseDefault = false;

                IFCUnit areaUnitType = IFCUnit.AreaUnit;
                IFCSIPrefix? areaPrefix = null;
                IFCSIUnitName areaUnitName = IFCSIUnitName.Square_Metre;
                string areaConvName = null;

                FormatOptions areaFormatOptions = doc.GetUnits().GetFormatOptions(UnitType.UT_Area);
                switch (areaFormatOptions.DisplayUnits)
                {
                    case DisplayUnitType.DUT_SQUARE_METERS:
                        break;
                    case DisplayUnitType.DUT_SQUARE_CENTIMETERS:
                        areaPrefix = IFCSIPrefix.Centi;
                        break;
                    case DisplayUnitType.DUT_SQUARE_MILLIMETERS:
                        areaPrefix = IFCSIPrefix.Milli;
                        break;
                    case DisplayUnitType.DUT_SQUARE_FEET:
                        {
                            if (exportToCOBIE)
                                areaConvName = "foot";
                            else
                                areaConvName = "SQUARE FOOT";
                            areaConversionBased = true;
                        }
                        break;
                    case DisplayUnitType.DUT_SQUARE_INCHES:
                        {
                            if (exportToCOBIE)
                                areaConvName = "inch";
                            else
                                areaConvName = "SQUARE INCH";
                        }
                        areaConversionBased = true;
                        break;
                    default:
                        {
                            //Couldn't find display unit type conversion -- assuming foot
                            if (exportToCOBIE)
                                areaConvName = "foot";
                            else
                                areaConvName = "SQUARE FOOT";
                            areaConversionBased = true;
                            areaUseDefault = true;
                        }
                        break;
                }

                double areaScaleFactor = UnitUtils.ConvertFromInternalUnits(1.0, areaUseDefault ? DisplayUnitType.DUT_SQUARE_FEET : areaFormatOptions.DisplayUnits);
                IFCAnyHandle areaSiUnit = IFCInstanceExporter.CreateSIUnit(file, areaUnitType, areaPrefix, areaUnitName);
                if (areaConversionBased)
                {
                    double areaSIScaleFactor = areaScaleFactor * UnitUtils.ConvertFromInternalUnits(1.0, DisplayUnitType.DUT_SQUARE_METERS);
                    IFCAnyHandle areaDims = IFCInstanceExporter.CreateDimensionalExponents(file, 2, 0, 0, 0, 0, 0, 0); // area
                    IFCAnyHandle areaConvFactor = IFCInstanceExporter.CreateMeasureWithUnit(file, Toolkit.IFCDataUtil.CreateAsRatioMeasure(areaSIScaleFactor), areaSiUnit);
                    areaSiUnit = IFCInstanceExporter.CreateConversionBasedUnit(file, areaDims, areaUnitType, areaConvName, areaConvFactor);
                }

                unitSet.Add(areaSiUnit);      // created above, so unique.
                ExporterCacheManager.UnitsCache.AddUnit(UnitType.UT_Area, areaSiUnit, areaScaleFactor, 0.0);
            }

            {
                bool volumeConversionBased = false;
                bool volumeUseDefault = false;

                IFCUnit volumeUnitType = IFCUnit.VolumeUnit;
                IFCSIPrefix? volumePrefix = null;
                IFCSIUnitName volumeUnitName = IFCSIUnitName.Cubic_Metre;
                string volumeConvName = null;

                FormatOptions volumeFormatOptions = doc.GetUnits().GetFormatOptions(UnitType.UT_Volume);
                switch (volumeFormatOptions.DisplayUnits)
                {
                    case DisplayUnitType.DUT_CUBIC_METERS:
                        break;
                    case DisplayUnitType.DUT_CUBIC_CENTIMETERS:
                        volumePrefix = IFCSIPrefix.Centi;
                        break;
                    case DisplayUnitType.DUT_CUBIC_MILLIMETERS:
                        volumePrefix = IFCSIPrefix.Milli;
                        break;
                    case DisplayUnitType.DUT_CUBIC_FEET:
                        {
                            if (exportToCOBIE)
                                volumeConvName = "foot";
                            else
                                volumeConvName = "CUBIC FOOT";
                            volumeConversionBased = true;
                        }
                        break;
                    case DisplayUnitType.DUT_CUBIC_INCHES:
                        {
                            if (exportToCOBIE)
                                volumeConvName = "inch";
                            else
                                volumeConvName = "CUBIC INCH";
                        }
                        volumeConversionBased = true;
                        break;
                    default:
                        {
                            //Couldn't find display unit type conversion -- assuming foot
                            if (exportToCOBIE)
                                volumeConvName = "foot";
                            else
                                volumeConvName = "CUBIC FOOT";
                            volumeConversionBased = true;
                            volumeUseDefault = true;
                        }
                        break;
                }

                double volumeScaleFactor =
                    UnitUtils.ConvertFromInternalUnits(1.0, volumeUseDefault ? DisplayUnitType.DUT_CUBIC_FEET : volumeFormatOptions.DisplayUnits);
                IFCAnyHandle volumeSiUnit = IFCInstanceExporter.CreateSIUnit(file, volumeUnitType, volumePrefix, volumeUnitName);
                if (volumeConversionBased)
                {
                    double volumeSIScaleFactor = volumeScaleFactor * UnitUtils.ConvertFromInternalUnits(1.0, DisplayUnitType.DUT_CUBIC_METERS);
                    IFCAnyHandle volumeDims = IFCInstanceExporter.CreateDimensionalExponents(file, 3, 0, 0, 0, 0, 0, 0); // area
                    IFCAnyHandle volumeConvFactor = IFCInstanceExporter.CreateMeasureWithUnit(file, Toolkit.IFCDataUtil.CreateAsRatioMeasure(volumeSIScaleFactor), volumeSiUnit);
                    volumeSiUnit = IFCInstanceExporter.CreateConversionBasedUnit(file, volumeDims, volumeUnitType, volumeConvName, volumeConvFactor);
                }

                unitSet.Add(volumeSiUnit);      // created above, so unique.
                ExporterCacheManager.UnitsCache.AddUnit(UnitType.UT_Volume, volumeSiUnit, volumeScaleFactor, 0.0);
            }

            {
                IFCUnit unitType = IFCUnit.PlaneAngleUnit;
                IFCSIUnitName unitName = IFCSIUnitName.Radian;

                IFCAnyHandle planeAngleUnit = IFCInstanceExporter.CreateSIUnit(file, unitType, null, unitName);

                string convName = null;

                FormatOptions angleFormatOptions = doc.GetUnits().GetFormatOptions(UnitType.UT_Angle);
                bool angleUseDefault = false;
                switch (angleFormatOptions.DisplayUnits)
                {
                    case DisplayUnitType.DUT_DECIMAL_DEGREES:
                    case DisplayUnitType.DUT_DEGREES_AND_MINUTES:
                        convName = "DEGREE";
                        break;
                    case DisplayUnitType.DUT_GRADS:
                        convName = "GRAD";
                        break;
                    case DisplayUnitType.DUT_RADIANS:
                        break;
                    default:
                        angleUseDefault = true;
                        convName = "DEGREE";
                        break;
                }

                IFCAnyHandle dims = IFCInstanceExporter.CreateDimensionalExponents(file, 0, 0, 0, 0, 0, 0, 0);

                double angleScaleFactor = UnitUtils.Convert(1.0, angleUseDefault ? DisplayUnitType.DUT_DECIMAL_DEGREES : angleFormatOptions.DisplayUnits, DisplayUnitType.DUT_RADIANS);
                if (convName != null)
                {
                    IFCAnyHandle convFactor = IFCInstanceExporter.CreateMeasureWithUnit(file, Toolkit.IFCDataUtil.CreateAsRatioMeasure(angleScaleFactor), planeAngleUnit);
                    planeAngleUnit = IFCInstanceExporter.CreateConversionBasedUnit(file, dims, unitType, convName, convFactor);
                }
                unitSet.Add(planeAngleUnit);      // created above, so unique.
                ExporterCacheManager.UnitsCache.AddUnit(UnitType.UT_Angle, planeAngleUnit, 1.0 / angleScaleFactor, 0.0);
            }

            // Mass
            IFCAnyHandle massSIUnit = null;
            {
                massSIUnit = CreateSIUnit(file, UnitType.UT_Mass, IFCUnit.MassUnit, IFCSIUnitName.Gram, IFCSIPrefix.Kilo, null);
                // If we are exporting to GSA standard, we will override kg with pound below.
                if (!exportToCOBIE)
                    unitSet.Add(massSIUnit);      // created above, so unique.
            }

            // Time -- support seconds only.
            IFCAnyHandle timeSIUnit = null;
            {
                timeSIUnit = CreateSIUnit(file, null, IFCUnit.TimeUnit, IFCSIUnitName.Second, null, null);
                unitSet.Add(timeSIUnit);      // created above, so unique.
            }

            // Frequency = support Hertz only.
            {
                IFCAnyHandle frequencySIUnit = CreateSIUnit(file, null, IFCUnit.FrequencyUnit, IFCSIUnitName.Hertz, null, null);
                unitSet.Add(frequencySIUnit);      // created above, so unique.
            }

            // Temperature
            IFCAnyHandle tempBaseSIUnit = null;
            {
                // Base SI unit for temperature.
                tempBaseSIUnit = CreateSIUnit(file, null, IFCUnit.ThermoDynamicTemperatureUnit, IFCSIUnitName.Kelvin, null, null);

                // We are going to have two entries: one for thermodynamic temperature (default), and one for color temperature.
                FormatOptions tempFormatOptions = doc.GetUnits().GetFormatOptions(UnitType.UT_HVAC_Temperature);
                IFCSIUnitName thermalTempUnit;
                double offset = 0.0;
                switch (tempFormatOptions.DisplayUnits)
                {
                    case DisplayUnitType.DUT_CELSIUS:
                    case DisplayUnitType.DUT_FAHRENHEIT:
                        thermalTempUnit = IFCSIUnitName.Degree_Celsius;
                        offset = -273.15;
                        break;
                    default:
                        thermalTempUnit = IFCSIUnitName.Kelvin;
                        break;
                }

                IFCAnyHandle temperatureSIUnit = null;
                if (thermalTempUnit != IFCSIUnitName.Kelvin)
                    temperatureSIUnit = IFCInstanceExporter.CreateSIUnit(file, IFCUnit.ThermoDynamicTemperatureUnit, null, thermalTempUnit);
                else
                    temperatureSIUnit = tempBaseSIUnit;
                ExporterCacheManager.UnitsCache.AddUnit(UnitType.UT_HVAC_Temperature, temperatureSIUnit, 1.0, offset);

                unitSet.Add(temperatureSIUnit);      // created above, so unique.

                // Color temperature.
                // We don't add the color temperature to the unit set; it will be explicitly used.
                IFCAnyHandle colorTempSIUnit = tempBaseSIUnit;
                ExporterCacheManager.UnitsCache["COLORTEMPERATURE"] = colorTempSIUnit;
            }

            // Thermal transmittance - support metric W/(m^2 * K) = kg/(K * s^3) only.
            {
                ISet<IFCAnyHandle> elements = new HashSet<IFCAnyHandle>();
                elements.Add(IFCInstanceExporter.CreateDerivedUnitElement(file, massSIUnit, 1));
                elements.Add(IFCInstanceExporter.CreateDerivedUnitElement(file, tempBaseSIUnit, -1));
                elements.Add(IFCInstanceExporter.CreateDerivedUnitElement(file, timeSIUnit, -3));

                IFCAnyHandle thermalTransmittanceUnit = IFCInstanceExporter.CreateDerivedUnit(file, elements,
                    IFCDerivedUnitEnum.ThermalTransmittanceUnit, null);
                unitSet.Add(thermalTransmittanceUnit);
            }

            // Volumetric Flow Rate - support metric m^3/s only.
            {
                ISet<IFCAnyHandle> elements = new HashSet<IFCAnyHandle>();
                elements.Add(IFCInstanceExporter.CreateDerivedUnitElement(file, lenSIBaseUnit, 3));
                elements.Add(IFCInstanceExporter.CreateDerivedUnitElement(file, timeSIUnit, -1));

                IFCAnyHandle volumetricFlowRateUnit = IFCInstanceExporter.CreateDerivedUnit(file, elements,
                    IFCDerivedUnitEnum.VolumetricFlowRateUnit, null);
                unitSet.Add(volumetricFlowRateUnit);

                double volumetricFlowRateFactor = UnitUtils.ConvertFromInternalUnits(1.0, DisplayUnitType.DUT_CUBIC_METERS_PER_SECOND);
                ExporterCacheManager.UnitsCache.AddUnit(UnitType.UT_HVAC_Airflow, volumetricFlowRateUnit, volumetricFlowRateFactor, 0.0);
            }

            // Electrical current - support metric ampere only.
            {
                IFCAnyHandle currentSIUnit = CreateSIUnit(file, UnitType.UT_Electrical_Current, IFCUnit.ElectricCurrentUnit, IFCSIUnitName.Ampere,
                    null, DisplayUnitType.DUT_AMPERES);
                unitSet.Add(currentSIUnit);      // created above, so unique.
            }

            // Electrical voltage - support metric volt only.
            {
                IFCAnyHandle voltageSIUnit = CreateSIUnit(file, UnitType.UT_Electrical_Potential, IFCUnit.ElectricVoltageUnit, IFCSIUnitName.Volt,
                    null, DisplayUnitType.DUT_VOLTS);
                unitSet.Add(voltageSIUnit);      // created above, so unique.
            }

            // Power - support metric watt only.
            {
                IFCAnyHandle voltageSIUnit = CreateSIUnit(file, UnitType.UT_HVAC_Power, IFCUnit.PowerUnit, IFCSIUnitName.Watt,
                    null, DisplayUnitType.DUT_WATTS);
                unitSet.Add(voltageSIUnit);      // created above, so unique.
            }

            // Force - support newtons (N) and kN only.
            {
                IFCSIPrefix? prefix = null;
                FormatOptions forceFormatOptions = doc.GetUnits().GetFormatOptions(UnitType.UT_Force);
                DisplayUnitType dut = forceFormatOptions.DisplayUnits;
                switch (dut)
                {
                    case DisplayUnitType.DUT_NEWTONS:
                        break;
                    case DisplayUnitType.DUT_KILONEWTONS:
                        prefix = IFCSIPrefix.Kilo;
                        break;
                    default:
                        dut = DisplayUnitType.DUT_NEWTONS;
                        break;
                }

                IFCAnyHandle forceSIUnit = CreateSIUnit(file, UnitType.UT_Force, IFCUnit.ForceUnit, IFCSIUnitName.Newton,
                    prefix, dut);
                unitSet.Add(forceSIUnit);      // created above, so unique.
            }

            // Illuminance
            {
                IFCSIPrefix? prefix = null;
                IFCAnyHandle luxSIUnit = CreateSIUnit(file, UnitType.UT_Electrical_Illuminance, IFCUnit.IlluminanceUnit, IFCSIUnitName.Lux,
                    prefix, DisplayUnitType.DUT_LUX);
                unitSet.Add(luxSIUnit);      // created above, so unique.
                ExporterCacheManager.UnitsCache["LUX"] = luxSIUnit;
            }

            // Luminous Flux
            IFCAnyHandle lumenSIUnit = null;
            {
                IFCSIPrefix? prefix = null;
                lumenSIUnit = CreateSIUnit(file, UnitType.UT_Electrical_Luminous_Flux, IFCUnit.LuminousFluxUnit, IFCSIUnitName.Lumen,
                    prefix, DisplayUnitType.DUT_LUMENS);
                unitSet.Add(lumenSIUnit);      // created above, so unique.
            }

            // Luminous Intensity
            {
                IFCSIPrefix? prefix = null;
                IFCAnyHandle candelaSIUnit = CreateSIUnit(file, UnitType.UT_Electrical_Luminous_Intensity, IFCUnit.LuminousIntensityUnit, IFCSIUnitName.Candela,
                    prefix, DisplayUnitType.DUT_CANDELAS);
                unitSet.Add(candelaSIUnit);      // created above, so unique.
            }

            // Luminous Efficacy - support lm/W only.
            {
                ISet<IFCAnyHandle> elements = new HashSet<IFCAnyHandle>();
                elements.Add(IFCInstanceExporter.CreateDerivedUnitElement(file, massSIUnit, -1));
                elements.Add(IFCInstanceExporter.CreateDerivedUnitElement(file, lenSIBaseUnit, -2));
                elements.Add(IFCInstanceExporter.CreateDerivedUnitElement(file, timeSIUnit, 3));
                elements.Add(IFCInstanceExporter.CreateDerivedUnitElement(file, lumenSIUnit, 1));

                IFCAnyHandle luminousEfficacyUnit = IFCInstanceExporter.CreateDerivedUnit(file, elements,
                    IFCDerivedUnitEnum.UserDefined, "Luminous Efficacy");

                double electricalEfficacyFactor = UnitUtils.ConvertFromInternalUnits(1.0, DisplayUnitType.DUT_LUMENS_PER_WATT);
                ExporterCacheManager.UnitsCache.AddUnit(UnitType.UT_Electrical_Efficacy, luminousEfficacyUnit, electricalEfficacyFactor, 0.0);
                ExporterCacheManager.UnitsCache["LUMINOUSEFFICACY"] = luminousEfficacyUnit;
            }

            // Currency
            {
                IFCCurrencyType? currencyType = null;

                // Some of these are guesses, since multiple currencies may use the same symbol, but no detail is given on which currency
                // is being used.
                FormatOptions currencyFormatOptions = doc.GetUnits().GetFormatOptions(UnitType.UT_Currency);
                UnitSymbolType ust = currencyFormatOptions.UnitSymbol;
                switch (ust)
                {
                    case UnitSymbolType.UST_DOLLAR:
                        currencyType = IFCCurrencyType.USD;
                        break;
                    case UnitSymbolType.UST_EURO_PREFIX:
                    case UnitSymbolType.UST_EURO_SUFFIX:
                        currencyType = IFCCurrencyType.EUR;
                        break;
                    case UnitSymbolType.UST_POUND:
                        currencyType = IFCCurrencyType.GBP;
                        break;
                    case UnitSymbolType.UST_CHINESE_HONG_KONG_SAR:
                        currencyType = IFCCurrencyType.HKD;
                        break;
                    case UnitSymbolType.UST_KRONER:
                        currencyType = IFCCurrencyType.NOK;
                        break;
                    case UnitSymbolType.UST_SHEQEL:
                        currencyType = IFCCurrencyType.ILS;
                        break;
                    case UnitSymbolType.UST_YEN:
                        currencyType = IFCCurrencyType.JPY;
                        break;
                    case UnitSymbolType.UST_WON:
                        currencyType = IFCCurrencyType.KRW;
                        break;
                    case UnitSymbolType.UST_BAHT:
                        currencyType = IFCCurrencyType.THB;
                        break;
                    case UnitSymbolType.UST_DONG:
                        currencyType = IFCCurrencyType.VND;
                        break;
                }

                if (currencyType.HasValue)
                {
                    IFCAnyHandle currencyUnit = IFCInstanceExporter.CreateMonetaryUnit(file, currencyType.Value);
                    unitSet.Add(currencyUnit);      // created above, so unique.
                    // We will cache the currency, f we create it.  If we don't, we'll export currencies as numbers.
                    ExporterCacheManager.UnitsCache["CURRENCY"] = currencyUnit;
                }
            }

            // Pressure - support Pascal, kPa and MPa.
            {
                IFCSIPrefix? prefix = null;
                FormatOptions pressureFormatOptions = doc.GetUnits().GetFormatOptions(UnitType.UT_HVAC_Pressure);
                DisplayUnitType dut = pressureFormatOptions.DisplayUnits;
                switch (dut)
                {
                    case DisplayUnitType.DUT_PASCALS:
                        break;
                    case DisplayUnitType.DUT_KILOPASCALS:
                        prefix = IFCSIPrefix.Kilo;
                        break;
                    case DisplayUnitType.DUT_MEGAPASCALS:
                        prefix = IFCSIPrefix.Mega;
                        break;
                    default:
                        dut = DisplayUnitType.DUT_PASCALS;
                        break;
                }

                IFCAnyHandle pressureSIUnit = CreateSIUnit(file, UnitType.UT_HVAC_Pressure, IFCUnit.PressureUnit, IFCSIUnitName.Pascal,
                    prefix, dut);
                unitSet.Add(pressureSIUnit);      // created above, so unique.
            }

            // GSA only units.
            if (exportToCOBIE)
            {
                // Derived imperial mass unit
                {
                    IFCUnit unitType = IFCUnit.MassUnit;
                    IFCAnyHandle dims = IFCInstanceExporter.CreateDimensionalExponents(file, 0, 1, 0, 0, 0, 0, 0);
                    double factor = 0.45359237; // --> pound to kilogram
                    string convName = "pound";

                    IFCAnyHandle convFactor = IFCInstanceExporter.CreateMeasureWithUnit(file, Toolkit.IFCDataUtil.CreateAsRatioMeasure(factor), massSIUnit);
                    IFCAnyHandle massUnit = IFCInstanceExporter.CreateConversionBasedUnit(file, dims, unitType, convName, convFactor);
                    unitSet.Add(massUnit);      // created above, so unique.
                }

                // Air Changes per Hour
                {
                    IFCUnit unitType = IFCUnit.FrequencyUnit;
                    IFCAnyHandle dims = IFCInstanceExporter.CreateDimensionalExponents(file, 0, 0, -1, 0, 0, 0, 0);
                    double factor = 1.0 / 3600.0; // --> seconds to hours
                    string convName = "ACH";

                    IFCAnyHandle convFactor = IFCInstanceExporter.CreateMeasureWithUnit(file, Toolkit.IFCDataUtil.CreateAsRatioMeasure(factor), timeSIUnit);
                    IFCAnyHandle achUnit = IFCInstanceExporter.CreateConversionBasedUnit(file, dims, unitType, convName, convFactor);
                    unitSet.Add(achUnit);      // created above, so unique.
                    ExporterCacheManager.UnitsCache["ACH"] = achUnit;
                }
            }

            return IFCInstanceExporter.CreateUnitAssignment(file, unitSet);
        }

        /// <summary>
        /// Creates the global direction and sets the cardinal directions in 3D.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        private void CreateGlobalDirection(ExporterIFC exporterIFC)
        {
            IFCAnyHandle xDirPos = null;
            IFCAnyHandle xDirNeg = null;
            IFCAnyHandle yDirPos = null;
            IFCAnyHandle yDirNeg = null;
            IFCAnyHandle zDirPos = null;
            IFCAnyHandle zDirNeg = null;

            IFCFile file = exporterIFC.GetFile();
            IList<double> xxp = new List<double>();
            xxp.Add(1.0); xxp.Add(0.0); xxp.Add(0.0);
            xDirPos = IFCInstanceExporter.CreateDirection(file, xxp);

            IList<double> xxn = new List<double>();
            xxn.Add(-1.0); xxn.Add(0.0); xxn.Add(0.0);
            xDirNeg = IFCInstanceExporter.CreateDirection(file, xxn);

            IList<double> yyp = new List<double>();
            yyp.Add(0.0); yyp.Add(1.0); yyp.Add(0.0);
            yDirPos = IFCInstanceExporter.CreateDirection(file, yyp);

            IList<double> yyn = new List<double>();
            yyn.Add(0.0); yyn.Add(-1.0); yyn.Add(0.0);
            yDirNeg = IFCInstanceExporter.CreateDirection(file, yyn);

            IList<double> zzp = new List<double>();
            zzp.Add(0.0); zzp.Add(0.0); zzp.Add(1.0);
            zDirPos = IFCInstanceExporter.CreateDirection(file, zzp);

            IList<double> zzn = new List<double>();
            zzn.Add(0.0); zzn.Add(0.0); zzn.Add(-1.0);
            zDirNeg = IFCInstanceExporter.CreateDirection(file, zzn);

            ExporterIFCUtils.SetGlobal3DDirectionHandles(true, xDirPos, yDirPos, zDirPos);
            ExporterIFCUtils.SetGlobal3DDirectionHandles(false, xDirNeg, yDirNeg, zDirNeg);
        }

        /// <summary>
        /// Creates the global direction and sets the cardinal directions in 2D.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        private void CreateGlobalDirection2D(ExporterIFC exporterIFC)
        {
            IFCAnyHandle xDirPos2D = null;
            IFCAnyHandle xDirNeg2D = null;
            IFCAnyHandle yDirPos2D = null;
            IFCAnyHandle yDirNeg2D = null;
            IFCFile file = exporterIFC.GetFile();

            IList<double> xxp = new List<double>();
            xxp.Add(1.0); xxp.Add(0.0);
            xDirPos2D = IFCInstanceExporter.CreateDirection(file, xxp);

            IList<double> xxn = new List<double>();
            xxn.Add(-1.0); xxn.Add(0.0);
            xDirNeg2D = IFCInstanceExporter.CreateDirection(file, xxn);

            IList<double> yyp = new List<double>();
            yyp.Add(0.0); yyp.Add(1.0);
            yDirPos2D = IFCInstanceExporter.CreateDirection(file, yyp);

            IList<double> yyn = new List<double>();
            yyn.Add(0.0); yyn.Add(-1.0);
            yDirNeg2D = IFCInstanceExporter.CreateDirection(file, yyn);
            ExporterIFCUtils.SetGlobal2DDirectionHandles(true, xDirPos2D, yDirPos2D);
            ExporterIFCUtils.SetGlobal2DDirectionHandles(false, xDirNeg2D, yDirNeg2D);
        }

        /// <summary>
        /// Creates the global cartesian origin then sets the 3D and 2D origins.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        private void CreateGlobalCartesianOrigin(ExporterIFC exporterIFC)
        {

            IFCAnyHandle origin2d = null;
            IFCAnyHandle origin = null;

            IFCFile file = exporterIFC.GetFile();
            IList<double> measure = new List<double>();
            measure.Add(0.0); measure.Add(0.0); measure.Add(0.0);
            origin = IFCInstanceExporter.CreateCartesianPoint(file, measure);

            IList<double> measure2d = new List<double>();
            measure2d.Add(0.0); measure2d.Add(0.0);
            origin2d = IFCInstanceExporter.CreateCartesianPoint(file, measure2d);
            ExporterIFCUtils.SetGlobal3DOriginHandle(origin);
            ExporterIFCUtils.SetGlobal2DOriginHandle(origin2d);
        }

        private HashSet<IFCAnyHandle> RemoveContainedHandlesFromSet(ICollection<IFCAnyHandle> initialSet)
        {
            HashSet<IFCAnyHandle> filteredSet = new HashSet<IFCAnyHandle>();
            foreach (IFCAnyHandle initialHandle in initialSet)
            {
                if (ExporterCacheManager.ElementsInAssembliesCache.Contains(initialHandle))
                    continue;

                try
                {
                    if (!IFCAnyHandleUtil.HasRelDecomposes(initialHandle))
                        filteredSet.Add(initialHandle);
                }
                catch
                {
                }
            }
            return filteredSet;
        }

        /// <summary>
        /// Relate levels and products.
        /// </summary>
        /// <param name="exporterIFC">The IFC exporter object.</param>
        /// <param name="document">The document to relate the levels.</param>
        private void RelateLevels(ExporterIFC exporterIFC, Document document)
        {
            HashSet<IFCAnyHandle> buildingStoreys = new HashSet<IFCAnyHandle>();
            List<ElementId> levelIds = ExporterCacheManager.LevelInfoCache.LevelsByElevation;
            for (int ii = 0; ii < levelIds.Count; ii++)
            {
                ElementId levelId = levelIds[ii];
                IFCLevelInfo levelInfo = ExporterCacheManager.LevelInfoCache.GetLevelInfo(exporterIFC, levelId);
                if (levelInfo == null)
                    continue;

                // remove products that are aggregated (e.g., railings in stairs).
                Element level = document.GetElement(levelId);

                ICollection<IFCAnyHandle> relatedProductsToCheck = levelInfo.GetRelatedProducts();
                ICollection<IFCAnyHandle> relatedElementsToCheck = levelInfo.GetRelatedElements();

                // get coincident levels, if any.
                double currentElevation = levelInfo.Elevation;
                int nextLevelIdx = ii + 1;
                for (int jj = ii + 1; jj < levelIds.Count; jj++, nextLevelIdx++)
                {
                    ElementId nextLevelId = levelIds[jj];
                    IFCLevelInfo levelInfo2 = ExporterCacheManager.LevelInfoCache.GetLevelInfo(exporterIFC, nextLevelId);
                    if (levelInfo2 == null)
                        continue;

                    if (MathUtil.IsAlmostEqual(currentElevation, levelInfo2.Elevation))
                    {
                        foreach (IFCAnyHandle relatedProduct in levelInfo2.GetRelatedProducts())
                            relatedProductsToCheck.Add(relatedProduct);

                        foreach (IFCAnyHandle relatedElement in levelInfo2.GetRelatedElements())
                            relatedElementsToCheck.Add(relatedElement);
                    }
                    else
                        break;
                }

                // We may get stale handles in here; protect against this.
                HashSet<IFCAnyHandle> relatedProducts = RemoveContainedHandlesFromSet(relatedProductsToCheck);
                HashSet<IFCAnyHandle> relatedElements = RemoveContainedHandlesFromSet(relatedElementsToCheck);

                // skip coincident levels, if any.
                for (int jj = ii + 1; jj < nextLevelIdx; jj++)
                {
                    ElementId nextLevelId = levelIds[jj];
                    IFCLevelInfo levelInfo2 = ExporterCacheManager.LevelInfoCache.GetLevelInfo(exporterIFC, nextLevelId);
                    if (levelInfo2 == null)
                        continue;

                    if (!levelInfo.GetBuildingStorey().Equals(levelInfo2.GetBuildingStorey()))
                        levelInfo2.GetBuildingStorey().Delete();
                }
                ii = nextLevelIdx - 1;

                if (relatedProducts.Count == 0 && relatedElements.Count == 0)
                    levelInfo.GetBuildingStorey().Delete();
                else
                {
                    // We have decided to keep the level - export properties, quantities and classifications.
                    using (ProductWrapper productWrapper = ProductWrapper.Create(exporterIFC, false))
                    {
                        IFCAnyHandle buildingStoreyHandle = levelInfo.GetBuildingStorey();
                        buildingStoreys.Add(buildingStoreyHandle);
                    
                        // Add Property set, quantities and classification of Building Storey also to IFC
                        productWrapper.AddElement(level, buildingStoreyHandle, levelInfo, null, false);

                        ExporterUtil.ExportRelatedProperties(exporterIFC, level, productWrapper);
                    }
                }

                if (relatedProducts.Count > 0)
                {
                    HashSet<IFCAnyHandle> buildingProducts = RemoveContainedHandlesFromSet(relatedProducts);
                    IFCAnyHandle buildingStorey = levelInfo.GetBuildingStorey();
                    string guid = GUIDUtil.CreateSubElementGUID(level, (int)IFCBuildingStoreySubElements.RelAggregates);
                    ExporterCacheManager.ContainmentCache.SetGUIDForRelation(buildingStorey, guid);
                    ExporterCacheManager.ContainmentCache.AddRelations(buildingStorey, buildingProducts);
                }
                if (relatedElements.Count > 0)
                {
                    HashSet<IFCAnyHandle> buildingElements = RemoveContainedHandlesFromSet(relatedElements);
                    string guid = GUIDUtil.CreateSubElementGUID(level, (int)IFCBuildingStoreySubElements.RelContainedInSpatialStructure);
                    IFCInstanceExporter.CreateRelContainedInSpatialStructure(exporterIFC.GetFile(), guid, exporterIFC.GetOwnerHistoryHandle(), null, null, buildingElements, levelInfo.GetBuildingStorey());
                }
            }

            if (buildingStoreys.Count > 0)
            {
                IFCAnyHandle buildingHnd = ExporterCacheManager.BuildingHandle;
                ProjectInfo projectInfo = document.ProjectInformation;
                string guid = GUIDUtil.CreateSubElementGUID(projectInfo, (int)IFCBuildingSubElements.RelAggregatesBuildingStoreys);
                ExporterCacheManager.ContainmentCache.SetGUIDForRelation(buildingHnd, guid);
                ExporterCacheManager.ContainmentCache.AddRelations(buildingHnd, buildingStoreys);
            }
        }

        /// <summary>
        /// Clear all delegates.
        /// </summary>
        private void DelegateClear()
        {
            m_ElementExporter = null;
            m_PropertySetsToExport = null;
            m_QuantitiesToExport = null;
        }
    }
}