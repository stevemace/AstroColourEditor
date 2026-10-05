#:project ../src/AstroColour.Core/AstroColour.Core.csproj

// Generates samples/synthetic-nebula.xisf: a stretched RGB test image with red (Ha),
// teal (OIII) and gold regions, a neutral background and white stars.
// Run with: dotnet run samples/MakeSample.cs
using System.Buffers.Binary;
using System.Text;
using AstroColour.Core.Xisf;

const int W = 1600, H = 1000;
string outPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath("samples/MakeSample.cs"))!, "synthetic-nebula.xisf");

// Write a minimal XISF header with an empty Float32 image, then fill it via XisfFile.Save.
string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><xisf version=\"1.0\" xmlns=\"http://www.pixinsight.com/xisf\">" +
             $"<Image geometry=\"{W}:{H}:3\" sampleFormat=\"Float32\" bounds=\"0:1\" colorSpace=\"RGB\" location=\"attachment:4096:{W * H * 12}\">" +
             "<FITSKeyword name=\"OBJECT\" value=\"'Synthetic nebula'\" comment=\"Test image\"/></Image></xisf>";
var xmlBytes = Encoding.UTF8.GetBytes(xml);
using (var fs = File.Create(outPath))
{
    fs.Write("XISF0100"u8);
    var len = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(len, (uint)xmlBytes.Length);
    fs.Write(len);
    fs.Write(xmlBytes);
    fs.SetLength(4096 + W * H * 12);
}

var file = XisfFile.Load(outPath);
var rng = new Random(42);
float[] r = file.Planes[0], g = file.Planes[1], b = file.Planes[2];

(double x, double y, double radius, float r, float g, float b)[] blobs =
[
    (0.30, 0.45, 0.22, 0.85f, 0.20f, 0.25f),   // Ha red
    (0.62, 0.40, 0.18, 0.20f, 0.65f, 0.75f),   // OIII teal
    (0.75, 0.70, 0.14, 0.85f, 0.65f, 0.25f),   // gold
    (0.50, 0.75, 0.12, 0.35f, 0.35f, 0.85f),   // blue reflection
];

for (int y = 0; y < H; y++)
for (int x = 0; x < W; x++)
{
    int i = y * W + x;
    double fx = (double)x / W, fy = (double)y / H;
    float n = (float)(rng.NextDouble() * 0.02);
    float rr = 0.08f + n, gg = 0.08f + n, bb = 0.085f + n;
    foreach (var bl in blobs)
    {
        double d2 = ((fx - bl.x) * (fx - bl.x) * 2.56 + (fy - bl.y) * (fy - bl.y)) / (bl.radius * bl.radius);
        float w = (float)Math.Exp(-d2 * 2.5) * 0.9f;
        rr += bl.r * w; gg += bl.g * w; bb += bl.b * w;
    }
    r[i] = Math.Min(rr, 1f); g[i] = Math.Min(gg, 1f); b[i] = Math.Min(bb, 1f);
}

for (int s = 0; s < 900; s++)
{
    int cx = rng.Next(W), cy = rng.Next(H);
    double size = 0.6 + rng.NextDouble() * rng.NextDouble() * 3.5;
    for (int dy = -8; dy <= 8; dy++)
    for (int dx = -8; dx <= 8; dx++)
    {
        int x = cx + dx, y = cy + dy;
        if (x < 0 || y < 0 || x >= W || y >= H) continue;
        float w = (float)Math.Exp(-(dx * dx + dy * dy) / (2 * size * size));
        int i = y * W + x;
        r[i] = Math.Min(1f, r[i] + w); g[i] = Math.Min(1f, g[i] + w); b[i] = Math.Min(1f, b[i] + w * 0.97f);
    }
}

file.Save(outPath);
Console.WriteLine($"Wrote {outPath}");
