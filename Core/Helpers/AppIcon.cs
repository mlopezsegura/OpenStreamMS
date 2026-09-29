using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace OpenStreamMS.Core.Helpers
{
    /// <summary>
    /// Icono de marca de OpenStreamMS: cuadrado redondeado morado con un triangulo
    /// "play" blanco. Misma geometria que <c>wwwroot/favicon.svg</c>, la web de
    /// <c>docs/</c> y <c>generate-icon.ps1</c> (lienzo de 32 unidades, radio 7,
    /// triangulo 12,9 → 23,16 → 12,23). Si se cambia aqui, cambiarlo alli tambien.
    /// </summary>
    public static class AppIcon
    {
        static readonly Color Accent = Color.FromArgb(124, 58, 237);

        /// <summary>PNG de <paramref name="size"/>×<paramref name="size"/> px con fondo transparente.</summary>
        public static byte[] RenderPng(int size)
        {
            using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode     = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode   = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float k = size / 32f;
                float d = 14f * k; // diametro de las esquinas (radio 7)
                using var path = new GraphicsPath();
                path.AddArc(0, 0, d, d, 180, 90);
                path.AddArc(size - d, 0, d, d, 270, 90);
                path.AddArc(size - d, size - d, d, d, 0, 90);
                path.AddArc(0, size - d, d, d, 90, 90);
                path.CloseFigure();
                using var bg = new SolidBrush(Accent);
                g.FillPath(bg, path);

                g.FillPolygon(Brushes.White, new[]
                {
                    new PointF(12 * k,  9 * k),
                    new PointF(23 * k, 16 * k),
                    new PointF(12 * k, 23 * k),
                });
            }

            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }

        /// <summary>Fichero .ico multi-resolucion (entradas PNG) listo para escribir a disco.</summary>
        public static byte[] BuildIco(params int[] sizes)
        {
            if (sizes.Length == 0) sizes = [16, 24, 32, 48, 256];
            var images = sizes.Select(RenderPng).ToArray();

            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            // ICONDIR
            bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);

            // ICONDIRENTRY por tamaño
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte dim = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                bw.Write(dim); bw.Write(dim);
                bw.Write((byte)0); bw.Write((byte)0);
                bw.Write((ushort)1); bw.Write((ushort)32);
                bw.Write((uint)images[i].Length);
                bw.Write((uint)offset);
                offset += images[i].Length;
            }

            foreach (var img in images) bw.Write(img);
            bw.Flush();
            return ms.ToArray();
        }

        public static void WriteIco(string path) => File.WriteAllBytes(path, BuildIco());

        /// <summary>
        /// Icono para la bandeja: Windows elige la entrada que mejor encaja con el tamaño
        /// pedido (16 px a 100 %, 20-32 px con escalado), sin reescalar un bitmap de 16.
        /// </summary>
        public static Icon CreateIcon(Size size)
        {
            using var ms = new MemoryStream(BuildIco(16, 20, 24, 32, 48));
            return new Icon(ms, size);
        }
    }
}
