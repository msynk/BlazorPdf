using System.Text;
using BlazorPdf.Core.Fonts;

namespace BlazorPdf.Tests;

/// <summary>Phase 3.4: embedded CMap parsing maps character codes to CIDs.</summary>
public class CMapTests
{
    [Fact]
    public void Parses_codespace_ranges_and_cid_ranges()
    {
        const string cmap =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "1 begincidrange\n<0020> <007E> 1\nendcidrange\n" +
            "1 begincidchar\n<00A0> 200\nendcidchar\n" +
            "endcmap end end";
        var map = CMap.Parse(Encoding.ASCII.GetBytes(cmap));

        Assert.Equal(2, map.CodeLength);
        Assert.Equal(1, map.Lookup(0x20));       // range start -> CID 1
        Assert.Equal(1 + (0x41 - 0x20), map.Lookup(0x41)); // 'A' within range
        Assert.Equal(200, map.Lookup(0xA0));     // explicit cidchar
        Assert.Equal(0x1234, map.Lookup(0x1234)); // unmapped -> identity fallback
    }

    [Fact]
    public void Identity_maps_code_to_cid()
    {
        Assert.True(CMap.Identity.IsIdentity);
        Assert.Equal(0x4E2D, CMap.Identity.Lookup(0x4E2D));
    }
}
