﻿//
// BIM IFC library: this library works with Autodesk(R) Revit(R) to export IFC files containing model geometry.
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

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;

namespace BIM.IFC.Utility
{
    /// <summary>
    /// A container class for lists of solids and meshes.
    /// </summary>
    /// <remarks>
    /// Added in 2013, this is a migration from the IFCSolidMeshGeometryInfo class 
    /// that is used within the BeamExporter, BodyExporter, WallExporter, 
    /// FamilyInstanceExporter and RepresentationUtil classes.
    /// </remarks>
    public class SolidMeshGeometryInfo
    {
        private List<Solid> solidsList; // a list of collected solids
        private List<Mesh> meshesList;  // a list of collected meshes

        /// <summary>
        /// Creates a default SolidMeshGeometryInfo with empty solidsList and meshesList. 
        /// </summary>
        public SolidMeshGeometryInfo()
        {
            solidsList = new List<Solid>();
            meshesList = new List<Mesh>();
        }

        /// <summary>
        /// Appends a given Solid to the solidsList.
        /// </summary>
        /// <param name="geomElem">
        /// The Solid we are appending to the solidsList.
        /// </param>
        public void AddSolid(Solid solidToAdd)
        {
            solidsList.Add(solidToAdd);
        }

        /// <summary>
        /// Appends a given Mesh to the meshesList. 
        /// </summary>
        /// <param name="meshToAdd">
        /// The Mesh we are appending to the meshesList.
        /// </param>
        public void AddMesh(Mesh meshToAdd)
        {
            meshesList.Add(meshToAdd);
        }

        /// <summary>
        /// Returns the list of Solids. 
        /// </summary>
        public List<Solid> GetSolids()
        {
            return solidsList;
        }

        /// <summary>
        /// Returns the list of Meshes. 
        /// </summary>
        public List<Mesh> GetMeshes()
        {
            return meshesList;
        }

        /// <summary>
        /// Returns the number of Solids in solidsList.
        /// </summary>
        public int SolidsCount()
        {
            return solidsList.Count;
        }

        /// <summary>
        /// Returns the number of Meshes in meshesList.
        /// </summary>
        public int MeshesCount()
        {
            return meshesList.Count;
        }

        /// <summary>
        /// This method takes the solidsList and clips all of its solids between the given range.
        /// </summary>
        /// <param name="elem">
        /// The Element from which we obtain our BoundingBoxXYZ.
        /// </param>
        /// <param name="geomElem">
        /// The top-level GeometryElement from which to gather X and Y coordinates for the intersecting solid.
        /// </param>
        /// <param name="range">
        /// The IFCRange whose Z values we use to create an intersecting solid to clip the solids in this class's internal solidsList.
        /// If range boundaries are equal, method returns, performing no clippings.
        /// </param>
        public void ClipSolidsList(GeometryElement geomElem, IFCRange range)
        {
            if (geomElem == null)
            {
                throw new ArgumentNullException("geomElemToUse");
            }
            if (MathUtil.IsAlmostEqual(range.Start, range.End) || solidsList.Count == 0)
            {
                return;
            }

            double bottomZ;
            double boundDifference;
            if (range.Start < range.End)
            {
                bottomZ = range.Start;
                boundDifference = range.End - range.Start;
            }
            else
            {
                bottomZ = range.End;
                boundDifference = range.Start - range.End;
            }

            Autodesk.Revit.DB.Document doc = ExporterCacheManager.Document;
            Autodesk.Revit.ApplicationServices.Application application = doc.Application;
            Autodesk.Revit.Creation.Application app = application.Create;

            // create a new solid using the X and Y of the bounding box on the top level GeometryElement and the Z of the IFCRange
            BoundingBoxXYZ elemBoundingBox = geomElem.GetBoundingBox();
            XYZ pointA = new XYZ(elemBoundingBox.Min.X, elemBoundingBox.Min.Y, bottomZ);
            XYZ pointB = new XYZ(elemBoundingBox.Max.X, elemBoundingBox.Min.Y, bottomZ);
            XYZ pointC = new XYZ(elemBoundingBox.Max.X, elemBoundingBox.Max.Y, bottomZ);
            XYZ pointD = new XYZ(elemBoundingBox.Min.X, elemBoundingBox.Max.Y, bottomZ);

            List<Curve> perimeter = new List<Curve>();
            perimeter.Add(app.NewLineBound(pointA, pointB));
            perimeter.Add(app.NewLineBound(pointB, pointC));
            perimeter.Add(app.NewLineBound(pointC, pointD));
            perimeter.Add(app.NewLineBound(pointD, pointA));

            List<CurveLoop> boxPerimeterList = new List<CurveLoop>();
            boxPerimeterList.Add(CurveLoop.Create(perimeter));
            Solid intersectionSolid = GeometryCreationUtilities.CreateExtrusionGeometry(boxPerimeterList, XYZ.BasisZ, boundDifference);

            // cycle through the elements in solidsList and intersect them against intersectionSolid to create a new list
            List<Solid> clippedSolidsList = new List<Solid>();
            Solid currSolid;
            foreach (Solid solid in solidsList)
            {
                try
                {
                    // ExecuteBooleanOperation can throw if it fails.  In this case, just ignore the clipping.
                    currSolid = BooleanOperationsUtils.ExecuteBooleanOperation(solid, intersectionSolid, BooleanOperationsType.Intersect);
                    if (currSolid != null && currSolid.Volume != 0)
                    {
                        clippedSolidsList.Add(currSolid);
                    }
                }
                catch
                {
                }
            }
            solidsList = clippedSolidsList;
        }

        /// <summary>
        /// Splits any solid volumes which consist of multiple closed bodies into individual solids (and updates the storage accordingly).
        /// </summary>
        public void SplitSolidsList()
        {
            List<Solid> splitSolidsList = new List<Solid>();
            foreach (Solid solid in solidsList)
            {
                splitSolidsList.AddRange(GeometryUtil.SplitVolumes(solid));
            }
            solidsList = splitSolidsList;
        }
    }
}
