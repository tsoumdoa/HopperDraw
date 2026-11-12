using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace hopperdraw
{
    internal sealed class DrawingCursorCache : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct IconInfo { [MarshalAs(UnmanagedType.Bool)] internal bool Icon; internal uint XHotspot,YHotspot; internal IntPtr Mask,Color; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo { internal uint Size; internal int Width,Height; internal ushort Planes,BitCount; internal uint Compression,SizeImage; internal int XPels,YPels; internal uint ClrUsed,ClrImportant; }
        [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr CreateIconIndirect(ref IconInfo info);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("gdi32.dll",SetLastError=true)] private static extern IntPtr CreateDIBSection(IntPtr dc,ref BitmapInfo info,uint usage,out IntPtr bits,IntPtr section,uint offset);
        [DllImport("gdi32.dll",SetLastError=true)] private static extern IntPtr CreateBitmap(int width,int height,uint planes,uint bits,byte[] data);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        internal sealed class Resource : IDisposable
        {
            internal readonly Cursor Cursor;
            internal IntPtr Handle { get; private set; }
            internal Resource(IntPtr handle) { Handle=handle; Cursor=new Cursor(handle); LiveHandles++; }
            public void Dispose()
            {
                if(Handle==IntPtr.Zero) return;
                Cursor.Dispose(); DestroyIcon(Handle); Handle=IntPtr.Zero; LiveHandles--;
            }
        }
        private readonly Dictionary<(int,int),Resource> _cache=new Dictionary<(int,int),Resource>();
        internal static int LiveHandles { get; private set; }
        internal bool IsDisposed { get; private set; }
        internal static int SizeForDpi(int dpi) => dpi<=96 ? 32 : dpi<=144 ? 48 : 64;
        internal Cursor Get(int tool,int dpi)
        {
            if(IsDisposed) throw new ObjectDisposedException(nameof(DrawingCursorCache));
            var key=(tool,dpi);
            if(!_cache.TryGetValue(key,out var value)) { value=Create(tool,dpi); _cache.Add(key,value); }
            return value.Cursor;
        }
        internal static Resource Create(int tool,int dpi)
        {
            int size=SizeForDpi(dpi),center=size/2;
            using(var bitmap=new Bitmap(size,size,PixelFormat.Format32bppPArgb))
            {
                using(var g=Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.Transparent);
                    float reach=8*size/32f,gap=3*size/32f;
                    foreach(var ink in new[]{Color.FromArgb(245,245,245),Color.FromArgb(35,35,35)})
                    using(var pen=new Pen(ink,ink.R>100 ? 3 : 1))
                    {
                        g.DrawLine(pen,center-reach,center,center-gap,center); g.DrawLine(pen,center+gap,center,center+reach,center);
                        g.DrawLine(pen,center,center-reach,center,center-gap); g.DrawLine(pen,center,center+gap,center,center+reach);
                    }
                    g.SmoothingMode=SmoothingMode.AntiAlias;
                    var box=new RectangleF(center+6*size/32f,center+6*size/32f,9*size/32f,9*size/32f);
                    ToolGlyph.Draw(g,tool,box,Color.White,3*size/32f); ToolGlyph.Draw(g,tool,box,Color.FromArgb(35,35,35),size/32f);
                }
                IntPtr color=IntPtr.Zero,mask=IntPtr.Zero,handle=IntPtr.Zero;
                try
                {
                    var info=new BitmapInfo{Size=(uint)Marshal.SizeOf(typeof(BitmapInfo)),Width=size,Height=-size,Planes=1,BitCount=32};
                    color=CreateDIBSection(IntPtr.Zero,ref info,0,out var bits,IntPtr.Zero,0);
                    if(color==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var data=bitmap.LockBits(new Rectangle(0,0,size,size),ImageLockMode.ReadOnly,PixelFormat.Format32bppPArgb);
                    var pixels=new byte[size*size*4];
                    try { for(int y=0;y<size;y++) Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),pixels,y*size*4,size*4); }
                    finally { bitmap.UnlockBits(data); }
                    Marshal.Copy(pixels,0,bits,pixels.Length);
                    int stride=((size+15)/16)*2; var masks=new byte[stride*size];
                    for(int y=0;y<size;y++) for(int x=0;x<size;x++) if(pixels[(y*size+x)*4+3]==0) masks[y*stride+x/8]|=(byte)(0x80>>(x%8));
                    mask=CreateBitmap(size,size,1,1,masks);
                    if(mask==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var icon=new IconInfo{Icon=false,XHotspot=(uint)center,YHotspot=(uint)center,Color=color,Mask=mask};
                    handle=CreateIconIndirect(ref icon);
                    if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                    var resource=new Resource(handle); handle=IntPtr.Zero; return resource;
                }
                finally { if(handle!=IntPtr.Zero) DestroyIcon(handle); if(mask!=IntPtr.Zero) DeleteObject(mask); if(color!=IntPtr.Zero) DeleteObject(color); }
            }
        }
        // The controller must reset its assigned Cursor before calling this.
        public void Dispose() { if(IsDisposed) return; IsDisposed=true; foreach(var value in _cache.Values) value.Dispose(); _cache.Clear(); }
    }
}
