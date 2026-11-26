using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;

namespace hopperdraw
{
    // Pure boundary decorator. Null means retain the plain outline. Self intersections deliberately get
    // no topology repair (nor quadratic intersection scan): safe, but not necessarily pretty.
    internal static class RevisionCloud
    {
        internal const int MaxArcs = 4000;
        private static double Distance(PointF a, PointF b) => Math.Sqrt(((double)b.X-a.X)*(b.X-a.X)+((double)b.Y-a.Y)*(b.Y-a.Y));
        private static PointF Lerp(PointF a, PointF b, double t) => new PointF((float)(a.X+((double)b.X-a.X)*t), (float)(a.Y+((double)b.Y-a.Y)*t));
        internal static GraphicsPath Decorate(GraphicsPath plain, PointF[] vertices, double size, bool curve)
        {
            if (double.IsNaN(size) || double.IsInfinity(size) || size <= 0) return null;
            GraphicsPath result = null;
            try
            {
                if (curve) using (var flat = (GraphicsPath)plain.Clone()) { flat.Flatten(null, .2f); vertices = flat.PathPoints; }
                var loop = new List<PointF>();
                foreach (var p in vertices) if (loop.Count == 0 || p != loop[loop.Count-1]) loop.Add(p);
                if (loop.Count > 1 && loop[0] == loop[loop.Count-1]) loop.RemoveAt(loop.Count-1);
                if (loop.Count < 3 || !ShapeRenderer.ValidPoints(loop.ToArray())) return null;
                // Local coordinates avoid cancellation in large, translated drawings.
                double area = 0, extent = 0, perimeter = 0; var origin = loop[0];
                var lengths = new double[loop.Count];
                for (int i=0;i<loop.Count;i++)
                {
                    var a=loop[i]; var b=loop[(i+1)%loop.Count];
                    area += ((double)a.X-origin.X)*((double)b.Y-origin.Y)-((double)b.X-origin.X)*((double)a.Y-origin.Y);
                    extent = Math.Max(extent,Distance(origin,a));
                    perimeter += lengths[i] = Distance(a,b);
                }
                if (Math.Abs(area) <= extent*extent*1e-10 || perimeter <= 1e-7) return null;
                double step = Math.Max(size, perimeter/MaxArcs);
                List<PointF> chords = null;
                if (!curve)
                {
                    if (loop.Count > MaxArcs) return null;
                    // Bound doubles before converting to int, even for subnormal requested sizes.
                    while (lengths.Sum(len => Math.Max(1,Math.Round(len/step,MidpointRounding.AwayFromZero))) > MaxArcs) step *= 1.1;
                    chords = new List<PointF>();
                    for (int i=0;i<loop.Count;i++)
                    {
                        int count=(int)Math.Max(1,Math.Round(lengths[i]/step,MidpointRounding.AwayFromZero));
                        for (int j=0;j<count;j++) chords.Add(Lerp(loop[i],loop[(i+1)%loop.Count],(double)j/count));
                    }
                }
                else
                {
                    var cumulative = new double[loop.Count+1];
                    for (int i=0;i<loop.Count;i++) cumulative[i+1]=cumulative[i]+lengths[i];
                    PointF At(double d)
                    {
                        if (d >= perimeter) return loop[0];
                        int index=Array.BinarySearch(cumulative,d);
                        if (index<0) index=~index-1;
                        return Lerp(loop[index],loop[(index+1)%loop.Count],(d-cumulative[index])/lengths[index]);
                    }
                    // Reflow with a larger effective step if bend subdivisions would exhaust the budget.
                    for (int attempt=0;attempt<64;attempt++,step*=1.5)
                    {
                        chords=new List<PointF>();
                        int count=(int)Math.Max(3,Math.Min(MaxArcs,Math.Round(perimeter/step,MidpointRounding.AwayFromZero)));
                        bool Add(double start,double end,int depth)
                        {
                            var a=At(start); var b=At(end); double deviation=0;
                            // Test every flattened vertex in this interval, not just its midpoint: an S bend
                            // can cross the chord at the midpoint and still cut far across the base spline.
                            int first=Array.BinarySearch(cumulative,start); if(first<0) first=~first;
                            for(int k=first;k<cumulative.Length && cumulative[k]<end;k++)
                                deviation=Math.Max(deviation,GeometryUtilities.DistanceToSegment(At(cumulative[k]),a,b));
                            if (deviation>step/2 && depth<24)
                            {
                                double mid=(start+end)/2;
                                return Add(start,mid,depth+1) && Add(mid,end,depth+1);
                            }
                            if(chords.Count>=MaxArcs) return false;
                            chords.Add(a); return true;
                        }
                        bool fits=true;
                        for(int i=0;i<count && fits;i++) fits=Add(perimeter*i/count,perimeter*(i+1)/count,0);
                        if(fits) break;
                        chords=null;
                    }
                }
                if (chords == null || chords.Count < 3) return null;
                result=new GraphicsPath(FillMode.Winding);
                for(int i=0;i<chords.Count;i++) Arc(result,chords[i],chords[(i+1)%chords.Count],area>0 ? 1 : -1);
                if(result.PointCount==0) { result.Dispose(); return null; }
                result.CloseFigure(); return result;
            }
            catch (Exception ex)
            {
                result?.Dispose();
                System.Diagnostics.Debug.WriteLine("HopperDraw cloud retained plain boundary: "+ex.Message);
                return null;
            }
        }
        // Fixed 150 degree circular arc, represented by two 75 degree cubics. Positive shoelace area is
        // visually clockwise in Y-down, hence its outward normal is (dy,-dx).
        private static void Arc(GraphicsPath path,PointF a,PointF b,int orientation)
        {
            double length=Distance(a,b); if(length<1e-7) return;
            double ux=((double)b.X-a.X)/length, uy=((double)b.Y-a.Y)/length;
            double nx=uy*orientation, ny=-ux*orientation, angle=75*Math.PI/180;
            double radius=length/(2*Math.Sin(angle)), height=radius*(1-Math.Cos(angle));
            double k=4.0/3*Math.Tan(angle/4)*radius;
            PointF P(double x,double y) => new PointF((float)x,(float)y);
            var apex=P(((double)a.X+b.X)/2+nx*height,((double)a.Y+b.Y)/2+ny*height);
            var c1=P(a.X+k*(ux*Math.Cos(angle)+nx*Math.Sin(angle)),a.Y+k*(uy*Math.Cos(angle)+ny*Math.Sin(angle)));
            var c2=P(apex.X-k*ux,apex.Y-k*uy);
            var c3=P(apex.X+k*ux,apex.Y+k*uy);
            var c4=P(b.X-k*(ux*Math.Cos(angle)-nx*Math.Sin(angle)),b.Y-k*(uy*Math.Cos(angle)-ny*Math.Sin(angle)));
            path.AddBezier(a,c1,c2,apex); path.AddBezier(apex,c3,c4,b);
        }
    }
}
