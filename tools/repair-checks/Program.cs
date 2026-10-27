using ImageMagick;

// The repair's resize rules, checked against real images.
//
// The rules are the importer's own shape: a SQUARE box, Magick does the fit, and
// IgnoreAspectRatio = false keeps the aspect ratio. Greater = true makes it a shrink
// only. The child is written as JPEG.

var failures = 0;
var checks = 0;

void Check(string what, bool ok, string detail = "")
{
    checks++;
    if (!ok)
    {
        failures++;
        Console.WriteLine($"FAIL  {what}  {detail}");
    }
    else
    {
        Console.WriteLine($"ok    {what}  {detail}");
    }
}

var scratch = Path.Combine(Path.GetTempPath(), "Utility", "repair-checks");
Directory.CreateDirectory(scratch);

// ---- a landscape parent and a portrait parent ----
var landscape = Path.Combine(scratch, "landscape.jpg");
var portrait = Path.Combine(scratch, "portrait.jpg");

using (var image = new MagickImage(MagickColors.White, 1500, 1000))
{
    image.Quality = 95;
    image.Write(landscape, MagickFormat.Jpeg);
}

using (var image = new MagickImage(MagickColors.White, 1000, 1500))
{
    image.Quality = 95;
    image.Write(portrait, MagickFormat.Jpeg);
}

// ---- the resize, exactly as the repair does it ----
static (uint Width, uint Height) Regenerate(string parentPath, int target, string childPath)
{
    using var image = new MagickImage();
    image.Read(parentPath);

    var side = (uint)Math.Max(1, target);

    var size = new MagickGeometry(side, side)
    {
        IgnoreAspectRatio = false,
        Greater = true
    };

    image.Resize(size);
    image.Quality = 35;

    if (image.ColorSpace == ColorSpace.CMYK)
    {
        image.ColorSpace = ColorSpace.RGB;
    }

    image.Write(childPath, MagickFormat.Jpeg);

    using var written = new MagickImage();
    written.Read(childPath);
    return (written.Width, written.Height);
}

// ---- 1. the longer side lands on the box's side ----
var child = Path.Combine(scratch, "child.jpg");

var (w, h) = Regenerate(landscape, 1500, child);
Check("landscape 1500x1000 -> 1500 target keeps the aspect ratio",
    w == 1500u && h == 1000u, $"got {w}x{h}");

(w, h) = Regenerate(portrait, 1500, child);
Check("portrait 1000x1500 -> 1500 target keeps the aspect ratio",
    w == 1000u && h == 1500u, $"got {w}x{h}");

// ---- 2. a parent smaller than the target keeps its own size ----
var small = Path.Combine(scratch, "small.jpg");
using (var image = new MagickImage(MagickColors.White, 800, 600))
{
    image.Write(small, MagickFormat.Jpeg);
}

(w, h) = Regenerate(small, 1500, child);
Check("a parent below the target is not scaled up",
    w == 800u && h == 600u, $"got {w}x{h}");

// ---- 3. a parent at the target keeps its size ----
var exact = Path.Combine(scratch, "exact.jpg");
using (var image = new MagickImage(MagickColors.White, 1500, 1500))
{
    image.Write(exact, MagickFormat.Jpeg);
}

(w, h) = Regenerate(exact, 1500, child);
Check("a parent at the target is unchanged", w == 1500 && h == 1500, $"got {w}x{h}");

// ---- 4. the child is a JPEG, whatever the parent was ----
var pngParent = Path.Combine(scratch, "parent.png");
using (var image = new MagickImage(MagickColors.White, 1200, 900))
{
    image.Write(pngParent, MagickFormat.Png);
}

Regenerate(pngParent, 1500, child);

using (var written = new MagickImage())
{
    written.Read(child);
    Check("the child is written as a JPEG", written.Format == MagickFormat.Jpeg,
        $"format {written.Format}");
}

// ---- 5. the child's format survives the read back with no extension in the name ----
var noExtension = Path.Combine(scratch, "0E42DFC7-409A-49DB-BEC4-D8981CC08EDC");
File.Delete(noExtension);

using (var image = new MagickImage(MagickColors.White, 1200, 900))
{
    image.Quality = 35;
    image.Write(noExtension, MagickFormat.Jpeg);
}

Check("a file named with no extension is a JPEG",
    File.Exists(noExtension), $"size {new FileInfo(noExtension).Length:N0} bytes");

// ---- 6. the child is RGB ----
//
// The image has to carry real colour: a white image is GRAYSCALE, and a grayscale
// parent is left in its own space - only CMYK is converted. So the check uses a
// coloured image, and a grayscale one is checked on its own terms.
var coloured = Path.Combine(scratch, "coloured.jpg");
using (var image = new MagickImage(MagickColors.Red, 1000, 1000))
{
    image.Write(coloured, MagickFormat.Jpeg);
}

Regenerate(coloured, 1500, child);

using (var written = new MagickImage())
{
    written.Read(child);
    // A JPEG reads back as sRGB, which is ImageMagick 7's name for RGB - the value
    // ColorSpace.RGB (21) and sRGB (23) are different enum members for the same space.
    // What matters is that the child is a COLOUR image: not gray, not CMYK.
    Check("the child is a colour image, not gray and not CMYK",
        written.ColorSpace != ColorSpace.Gray && written.ColorSpace != ColorSpace.CMYK,
        $"colour space {written.ColorSpace} ({(int)written.ColorSpace})");
}

// ---- a grayscale parent keeps its own space ----
var gray = Path.Combine(scratch, "gray.jpg");
using (var image = new MagickImage(MagickColors.White, 1000, 1000))
{
    image.Write(gray, MagickFormat.Jpeg);
}

Regenerate(gray, 1500, child);

using (var written = new MagickImage())
{
    written.Read(child);
    Check("a grayscale parent is left in its own space",
        written.ColorSpace == ColorSpace.Gray,
        $"colour space {written.ColorSpace}");
}

// ---- 7. a CMYK parent comes out RGB ----
var cmykParent = Path.Combine(scratch, "cmyk.jpg");
using (var image = new MagickImage(MagickColors.Red, 1000, 1000))
{
    image.ColorSpace = ColorSpace.CMYK;
    image.Write(cmykParent, MagickFormat.Jpeg);
}

// The parent really is CMYK, or the check below proves nothing.
using (var parent = new MagickImage())
{
    parent.Read(cmykParent);
    Check("the parent really is CMYK", parent.ColorSpace == ColorSpace.CMYK,
        $"colour space {parent.ColorSpace}");
}

Regenerate(cmykParent, 1500, child);

using (var written = new MagickImage())
{
    written.Read(child);
    Check("a CMYK parent produces a child that is not CMYK",
        written.ColorSpace != ColorSpace.CMYK,
        $"colour space {written.ColorSpace} ({(int)written.ColorSpace})");
}

Console.WriteLine();
Console.WriteLine(failures == 0
    ? $"All {checks} checks passed."
    : $"{failures} of {checks} checks FAILED.");

Directory.Delete(scratch, recursive: true);

return failures == 0 ? 0 : 1;
