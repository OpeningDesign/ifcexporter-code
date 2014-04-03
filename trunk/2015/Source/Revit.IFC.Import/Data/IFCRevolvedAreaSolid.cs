﻿//
// Revit IFC Import library: this library works with Autodesk(R) Revit(R) to import IFC files.
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
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.IFC;
using Revit.IFC.Common.Utility;
using Revit.IFC.Common.Enums;
using Revit.IFC.Import.Enums;
using Revit.IFC.Import.Geometry;
using Revit.IFC.Import.Utility;

namespace Revit.IFC.Import.Data
{
    public class IFCRevolvedAreaSolid : IFCSweptAreaSolid
    {
        Transform m_Axis = null;

        double m_Angle = 0.0;

        /// <summary>
        /// The axis of rotation for the revolved area solid in the local coordinate system.
        /// </summary>
        public Transform Axis
        {
            get { return m_Axis; }
            protected set { m_Axis = value; }
        }

        /// <summary>
        /// The angle of the sweep.  The sweep will go from 0 to angle in the local coordinate system.
        /// </summary>
        public double Angle
        {
            get { return m_Angle; }
            protected set { m_Angle = value; }
        }

        protected IFCRevolvedAreaSolid()
        {
        }

        override protected void Process(IFCAnyHandle solid)
        {
            base.Process(solid);

            // We will not fail if the axis is not given, but instead assume it to be the identity in the LCS.
            IFCAnyHandle axis = IFCImportHandleUtil.GetRequiredInstanceAttribute(solid, "Axis", false);
            if (axis != null)
                Axis = IFCLocation.ProcessIFCAxis1Placement(axis);
            else
                Axis = Transform.Identity;

            bool found = false;
            Angle = IFCImportHandleUtil.GetRequiredScaledAngleAttribute(solid, "Angle", out found);
            // TODO: IFCImportFile.TheFile.Document.Application.IsValidAngle(Angle)
            if (!found || Angle < MathUtil.Eps())
                IFCImportFile.TheLog.LogError(solid.StepId, "revolve angle is invalid, aborting.", true);
        }

        private XYZ GetValidXVectorFromLoop(CurveLoop curveLoop, XYZ zVec, XYZ origin)
        {
            foreach (Curve curve in curveLoop)
            {
                IList<XYZ> pointsToCheck = new List<XYZ>();

                // If unbound, must be cyclic.
                if (!curve.IsBound)
                {
                    pointsToCheck.Add(curve.Evaluate(0, false));
                    pointsToCheck.Add(curve.Evaluate(Math.PI / 2.0, false));
                    pointsToCheck.Add(curve.Evaluate(Math.PI, false));
                }
                else
                {
                    pointsToCheck.Add(curve.Evaluate(0, true));
                    pointsToCheck.Add(curve.Evaluate(1.0, true));
                    if (curve.IsCyclic)
                        pointsToCheck.Add(curve.Evaluate(0.5, true));
                }

                foreach (XYZ pointToCheck in pointsToCheck)
                {
                    XYZ possibleVec = (pointToCheck - origin);
                    XYZ yVec = zVec.CrossProduct(possibleVec).Normalize();
                    if (yVec.IsZeroLength())
                        continue;
                    return yVec.CrossProduct(zVec);
                }
            }

            return null;
        }

        /// <summary>
        /// Return geometry for a particular representation item.
        /// </summary>
        /// <param name="shapeEditScope">The shape edit scope.</param>
        /// <param name="lcs">Local coordinate system for the geometry.</param>
        /// <param name="forceSolid">True if we require a Solid.</param>
        /// <param name="guid">The guid of an element for which represntation is being created.</param>
        /// <returns>The created Solid.</returns>
        protected override GeometryObject CreateGeometryInternal(
              IFCImportShapeEditScope shapeEditScope, Transform lcs, Transform scaledLcs, bool forceSolid, string guid)
        {
            Transform origLCS = (lcs == null) ? Transform.Identity : lcs;
            Transform revolvePosition = (Position == null) ? origLCS : origLCS.Multiply(Position);

            XYZ frameOrigin = revolvePosition.OfPoint(Axis.Origin);
            XYZ frameZVec = revolvePosition.OfVector(Axis.BasisZ);
            XYZ frameXVec = null;

            IList<CurveLoop> loops = GetTransformedCurveLoops(revolvePosition);
            if (loops == null || loops.Count() == 0)
                return null;
            
            frameXVec = GetValidXVectorFromLoop(loops[0], frameZVec, frameOrigin);
            if (frameXVec == null)
            {
                IFCImportFile.TheLog.LogError(Id, "Couldn't generate valid frame for IfcRevolvedAreaSolid.", false);
                return null;
            }
            XYZ frameYVec = frameZVec.CrossProduct(frameXVec);
            Frame coordinateFrame = new Frame(frameOrigin, frameXVec, frameYVec, frameZVec);

            SolidOptions solidOptions = new SolidOptions(GetMaterialElementId(shapeEditScope), shapeEditScope.GraphicsStyleId);
            return GeometryCreationUtilities.CreateRevolvedGeometry(coordinateFrame, loops, 0, Angle, solidOptions);
        }

        /// <summary>
        /// Create geometry for a particular representation item.
        /// </summary>
        /// <param name="shapeEditScope">The geometry creation scope.</param>
        /// <param name="lcs">Local coordinate system for the geometry, without scale.</param>
        /// <param name="scaledLcs">Local coordinate system for the geometry, including scale, potentially non-uniform.</param>
        /// <param name="forceSolid">True if a Solid is required.</param>
        /// <param name="guid">The guid of an element for which represntation is being created.</param>
        protected override void CreateShapeInternal(IFCImportShapeEditScope shapeEditScope, Transform lcs, Transform scaledLcs, bool forceSolid, string guid)
        {
            base.CreateShapeInternal(shapeEditScope, lcs, scaledLcs, forceSolid, guid);

            GeometryObject revolvedGeometry = CreateGeometryInternal(shapeEditScope, lcs, scaledLcs, forceSolid, guid);
            if (revolvedGeometry != null)
                shapeEditScope.AddGeometry(IFCSolidInfo.Create(Id, revolvedGeometry));
        }

        protected IFCRevolvedAreaSolid(IFCAnyHandle solid)
        {
            Process(solid);
        }

        /// <summary>
        /// Create an IFCRevolvedAreaSolid object from a handle of type IfcRevolvedAreaSolid.
        /// </summary>
        /// <param name="ifcSolid">The IFC handle.</param>
        /// <returns>The IFCRevolvedAreaSolid object.</returns>
        public static IFCRevolvedAreaSolid ProcessIFCRevolvedAreaSolid(IFCAnyHandle ifcSolid)
        {
            if (IFCAnyHandleUtil.IsNullOrHasNoValue(ifcSolid))
            {
                IFCImportFile.TheLog.LogNullError(IFCEntityType.IfcRevolvedAreaSolid);
                return null;
            }

            IFCEntity solid;
            if (!IFCImportFile.TheFile.EntityMap.TryGetValue(ifcSolid.StepId, out solid))
                solid = new IFCRevolvedAreaSolid(ifcSolid);
            return (solid as IFCRevolvedAreaSolid); 
        }
    }
}
