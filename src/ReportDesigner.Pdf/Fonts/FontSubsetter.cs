using System.Runtime.InteropServices;

namespace ReportDesigner.Pdf.Fonts;

/// <summary>
/// Réduit une police aux seuls glyphes utilisés, avec l'API hb_subset de HarfBuzz
/// (présente dans la bibliothèque native livrée avec HarfBuzzSharp, mais non exposée par son API .NET).
/// Les numéros de glyphes sont conservés (RETAIN_GIDS) : la mise en forme déjà calculée reste valable.
/// </summary>
internal static partial class FontSubsetter
{
    private const string Library = "libHarfBuzzSharp";

    // hb_subset_flags_t
    private const uint NoHinting = 0x1;
    private const uint RetainGids = 0x2;

    private static bool _unavailable;

    /// <summary>Renvoie la police réduite, ou null si le sous-ensemble est impossible (la police complète sera alors intégrée).</summary>
    public static byte[]? Subset(byte[] fontData, int faceIndex, IEnumerable<ushort> glyphs, IEnumerable<int> codePoints)
    {
        if (_unavailable || fontData.Length == 0) return null;
        try
        {
            return SubsetCore(fontData, faceIndex, glyphs, codePoints);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            _unavailable = true; // bibliothèque native sans hb_subset : intégration complète des polices
            return null;
        }
    }

    private static unsafe byte[]? SubsetCore(byte[] fontData, int faceIndex, IEnumerable<ushort> glyphs, IEnumerable<int> codePoints)
    {
        fixed (byte* data = fontData)
        {
            // HB_MEMORY_MODE_READONLY (1) : HarfBuzz lit directement le tableau épinglé, sans copie.
            var blob = hb_blob_create((IntPtr)data, (uint)fontData.Length, 1, IntPtr.Zero, IntPtr.Zero);
            var face = hb_face_create(blob, (uint)faceIndex);
            var input = hb_subset_input_create_or_fail();
            IntPtr subsetFace = IntPtr.Zero, subsetBlob = IntPtr.Zero;
            try
            {
                if (input == IntPtr.Zero) return null;
                hb_subset_input_set_flags(input, NoHinting | RetainGids);

                var glyphSet = hb_subset_input_glyph_set(input);
                hb_set_add(glyphSet, 0); // .notdef
                foreach (var glyph in glyphs) hb_set_add(glyphSet, glyph);

                var unicodeSet = hb_subset_input_unicode_set(input);
                foreach (var codePoint in codePoints) hb_set_add(unicodeSet, (uint)codePoint);

                subsetFace = hb_subset_or_fail(face, input);
                if (subsetFace == IntPtr.Zero) return null;

                subsetBlob = hb_face_reference_blob(subsetFace);
                var pointer = hb_blob_get_data(subsetBlob, out var length);
                if (pointer == IntPtr.Zero || length == 0) return null;

                var result = new byte[length];
                Marshal.Copy(pointer, result, 0, (int)length);
                return result;
            }
            finally
            {
                if (subsetBlob != IntPtr.Zero) hb_blob_destroy(subsetBlob);
                if (subsetFace != IntPtr.Zero) hb_face_destroy(subsetFace);
                if (input != IntPtr.Zero) hb_subset_input_destroy(input);
                hb_face_destroy(face);
                hb_blob_destroy(blob);
            }
        }
    }

    [LibraryImport(Library)] private static partial IntPtr hb_blob_create(IntPtr data, uint length, int mode, IntPtr userData, IntPtr destroy);
    [LibraryImport(Library)] private static partial void hb_blob_destroy(IntPtr blob);
    [LibraryImport(Library)] private static partial IntPtr hb_blob_get_data(IntPtr blob, out uint length);
    [LibraryImport(Library)] private static partial IntPtr hb_face_create(IntPtr blob, uint index);
    [LibraryImport(Library)] private static partial void hb_face_destroy(IntPtr face);
    [LibraryImport(Library)] private static partial IntPtr hb_face_reference_blob(IntPtr face);
    [LibraryImport(Library)] private static partial void hb_set_add(IntPtr set, uint codePoint);
    [LibraryImport(Library)] private static partial IntPtr hb_subset_input_create_or_fail();
    [LibraryImport(Library)] private static partial void hb_subset_input_destroy(IntPtr input);
    [LibraryImport(Library)] private static partial void hb_subset_input_set_flags(IntPtr input, uint flags);
    [LibraryImport(Library)] private static partial IntPtr hb_subset_input_glyph_set(IntPtr input);
    [LibraryImport(Library)] private static partial IntPtr hb_subset_input_unicode_set(IntPtr input);
    [LibraryImport(Library)] private static partial IntPtr hb_subset_or_fail(IntPtr face, IntPtr input);
}
