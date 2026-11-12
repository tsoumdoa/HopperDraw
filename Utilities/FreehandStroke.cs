using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Rhino.Geometry;

namespace hopperdraw
{
    internal static class FreehandStroke
    {
        internal const int MaxRaw=8192, MaxOutput=2048;
        internal const double CloseExtent=18, CloseTravel=36;
        internal static CurveShape Process(IList<Point3d> raw, double zoom, SmoothingLevel level, float closeScale, bool allowClose = true)
        {
            if(raw.Count<2 || !StyleValidation.Finite(zoom) || zoom<=0) return null;
            double length=0, extent=0;
            for(int i=1;i<raw.Count;i++) { length+=raw[i].DistanceTo(raw[i-1]); extent=Math.Max(extent,raw[i].DistanceTo(raw[0])); }
            if(length*zoom<6 || extent*zoom<3) return null;
            bool closed=allowClose && raw[raw.Count-1].DistanceTo(raw[0])<=9*closeScale && extent*zoom>=CloseExtent && length*zoom>=CloseTravel;
            var input=raw.ToList(); if(closed) input.Add(input[0]);
            var resampled=Resample(input,Math.Max(2/zoom,(length+(closed?raw[raw.Count-1].DistanceTo(raw[0]):0))/(MaxOutput-2)));
            if(closed && resampled.Count>1) resampled.RemoveAt(resampled.Count-1);
            int kernel=level==SmoothingLevel.Low?3:level==SmoothingLevel.High?9:5;
            var smooth=new List<Point3d>();
            for(int i=0;i<resampled.Count;i++)
            {
                if(!closed && (i==0 || i==resampled.Count-1)) { smooth.Add(resampled[i]); continue; }
                double x=0,y=0; int n=0;
                for(int j=-kernel/2;j<=kernel/2;j++)
                {
                    int k=i+j; if(closed) k=(k+resampled.Count)%resampled.Count;
                    if(k<0 || k>=resampled.Count) continue;
                    x+=resampled[k].X; y+=resampled[k].Y; n++;
                }
                smooth.Add(new Point3d(x/n,y/n,0));
            }
            double tolerance=(level==SmoothingLevel.Low?.75:level==SmoothingLevel.High?3:1.5)/zoom;
            List<Point3d> output;
            if(closed)
            {
                // Split at the farthest point: never run endpoint RDP on coincident loop endpoints.
                int split=Enumerable.Range(1,smooth.Count-1).OrderByDescending(i=>smooth[i].DistanceTo(smooth[0])).First();
                var a=Simplify(smooth.Take(split+1).ToList(),tolerance);
                var b=Simplify(smooth.Skip(split).Concat(new[]{smooth[0]}).ToList(),tolerance);
                output=a.Take(a.Count-1).Concat(b.Take(b.Count-1)).ToList();
                if(!NonCollinear(output)) return Process(raw,zoom,level,closeScale,false);
            }
            else output=Simplify(smooth,tolerance);
            // Guard the rendered spline, not only its control polygon. Add dense smoothed points back
            // if simplification creates a cardinal-spline excursion greater than the screen error budget.
            var shape=new CurveShape(output,closed);
            if(Deviates(shape,smooth,Math.Max(3/zoom,tolerance*2))) shape.Points=smooth;
            if(shape.Points.Count>MaxOutput)
            {
                var bounded=new List<Point3d>();
                for(int i=0;i<MaxOutput;i++) bounded.Add(shape.Points[(int)((long)i*(shape.Points.Count-1)/(MaxOutput-1))]);
                shape.Points=bounded;
            }
            return shape.Points.Distinct().Count()<2 ? null : shape;
        }
        internal static bool NonCollinear(IList<Point3d> points)
        {
            if(points.Distinct().Count()<3) return false;
            var a=points[0]; var b=points.First(p=>p!=a);
            return points.Any(p=>Math.Abs((b.X-a.X)*(p.Y-a.Y)-(b.Y-a.Y)*(p.X-a.X))>1e-5);
        }
        private static List<Point3d> Resample(IList<Point3d> points,double spacing)
        {
            var result=new List<Point3d>{points[0]}; double remaining=spacing;
            for(int i=1;i<points.Count;i++)
            {
                var a=points[i-1]; var b=points[i]; double distance=a.DistanceTo(b);
                while(distance>=remaining && result.Count<MaxRaw-1)
                {
                    a=a+(b-a)*(remaining/distance); result.Add(a); distance=a.DistanceTo(b); remaining=spacing;
                }
                remaining-=distance;
            }
            if(result.Last()!=points.Last()) result.Add(points.Last()); return result;
        }
        private static List<Point3d> Simplify(List<Point3d> points,double tolerance)
        {
            if(points.Count<3) return points;
            var keep=new bool[points.Count]; keep[0]=keep[points.Count-1]=true;
            var stack=new Stack<int[]>(); stack.Push(new[]{0,points.Count-1});
            while(stack.Count>0)
            {
                var span=stack.Pop(); double max=tolerance; int index=-1;
                for(int i=span[0]+1;i<span[1];i++)
                {
                    double d=GeometryUtilities.DistanceToSegment(points[i].ToPointF(),points[span[0]].ToPointF(),points[span[1]].ToPointF());
                    if(d>max) { max=d; index=i; }
                }
                if(index<0) continue;
                keep[index]=true; stack.Push(new[]{span[0],index}); stack.Push(new[]{index,span[1]});
            }
            return points.Where((p,i)=>keep[i]).ToList();
        }
        private static bool Deviates(CurveShape curve,List<Point3d> smooth,double tolerance)
        {
            foreach(var stroke in AnnotationGeometry.Strokes(curve)) foreach(var p in stroke)
            {
                bool near=false;
                for(int i=1;i<smooth.Count;i++) if(GeometryUtilities.DistanceToSegment(p,smooth[i-1].ToPointF(),smooth[i].ToPointF())<=tolerance) { near=true; break; }
                if(!near && (!curve.Closed || GeometryUtilities.DistanceToSegment(p,smooth.Last().ToPointF(),smooth[0].ToPointF())>tolerance)) return true;
            }
            return false;
        }
    }
}
