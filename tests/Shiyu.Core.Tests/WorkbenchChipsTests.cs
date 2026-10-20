using Shiyu.Core;

namespace Shiyu.Core.Tests;

public class WorkbenchChipsTests
{
    [Fact]
    public void A_plain_text_entry_offers_copy_and_save_as_new()
    {
        var chips = WorkbenchChips.For(EntryKind.Text, EntrySubtype.None);

        Assert.Equal(new[] { "copy", "save-as-new" }, chips.Select(chip => chip.Action));
    }

    [Fact]
    public void A_link_offers_open_copy_and_save_as_new()
    {
        var chips = WorkbenchChips.For(EntryKind.Text, EntrySubtype.Link);

        Assert.Equal(new[] { "open-uri", "copy", "save-as-new" }, chips.Select(chip => chip.Action));
    }

    [Fact]
    public void An_email_leads_with_compose()
    {
        var chips = WorkbenchChips.For(EntryKind.Text, EntrySubtype.Email);

        Assert.Equal(new[] { "open-uri", "copy", "save-as-new" }, chips.Select(chip => chip.Action));
    }

    [Fact]
    public void A_local_path_offers_open_locate_and_copy()
    {
        var chips = WorkbenchChips.For(EntryKind.Text, EntrySubtype.LocalPath);

        Assert.Equal(new[] { "open-path", "locate-path", "copy" }, chips.Select(chip => chip.Action));
    }

    [Fact]
    public void A_color_only_copies()
    {
        var chips = WorkbenchChips.For(EntryKind.Text, EntrySubtype.Color);

        Assert.Equal(new[] { "copy" }, chips.Select(chip => chip.Action));
    }

    [Fact]
    public void Files_offer_open_locate_and_copy_path()
    {
        var chips = WorkbenchChips.For(EntryKind.Files, EntrySubtype.None);

        Assert.Equal(new[] { "open-path", "locate-path", "copy" }, chips.Select(chip => chip.Action));
    }

    [Fact]
    public void An_image_only_copies_itself()
    {
        var chips = WorkbenchChips.For(EntryKind.Image, EntrySubtype.None);

        Assert.Equal(new[] { "copy-image" }, chips.Select(chip => chip.Action));
    }
}
