using Shiyu.Core;

namespace Shiyu.Core.Tests;

/// <summary>
/// The hover actions are user-configured data with rules attached: unknown
/// and duplicate ids must not survive a hand-edited settings file, the order
/// the user chose is the order that shows, and an action the entry cannot
/// honour is absent rather than present and failing.
/// </summary>
public class HoverActionTests
{
    [Fact]
    public void The_default_order_covers_every_action_exactly_once()
    {
        Assert.Equal(HoverActions.All.Length, HoverActions.All.Distinct().Count());
        Assert.Contains("copy", HoverActions.All);
        Assert.Contains("delete", HoverActions.All);
        Assert.Equal(HoverActions.All, new AppSettings().BarActions);
    }

    [Fact]
    public void Sanitising_keeps_the_users_order_and_drops_the_rest()
    {
        var sanitised = HoverActions.Sanitise(["delete", "nonsense", "copy", "delete"]);

        Assert.Equal(["delete", "copy"], sanitised);
    }

    [Fact]
    public void An_empty_or_fully_unknown_choice_falls_back_to_the_default_order()
    {
        Assert.Equal(HoverActions.All, HoverActions.Sanitise([]));
        Assert.Equal(HoverActions.All, HoverActions.Sanitise(["nope", "also-nope"]));
    }

    [Fact]
    public void Plain_paste_is_hidden_for_images()
    {
        var available = HoverActions.AvailableFor(
            HoverActions.All, EntryKind.Image, hasOriginal: true);

        Assert.DoesNotContain("plain", available);
        Assert.Contains("copy", available);
    }

    [Fact]
    public void Open_and_locate_need_an_original_on_disk()
    {
        var withOriginal = HoverActions.AvailableFor(
            HoverActions.All, EntryKind.Image, hasOriginal: true);
        var without = HoverActions.AvailableFor(
            HoverActions.All, EntryKind.Image, hasOriginal: false);

        Assert.Contains("open", withOriginal);
        Assert.Contains("locate", withOriginal);
        Assert.DoesNotContain("open", without);
        Assert.DoesNotContain("locate", without);
    }

    [Fact]
    public void A_fresh_install_has_no_action_sound()
    {
        Assert.False(new AppSettings().ActionSound);
    }

    [Fact]
    public void A_former_default_action_list_upgrades_to_include_new_actions()
    {
        var path = Path.Combine(Path.GetTempPath(), "shiyu-actions", Guid.NewGuid().ToString("N"), "s.json");

        try
        {
            // Saved back when favourites and notes did not exist: exactly the
            // old default, not a choice.
            new AppSettings
            {
                BarActions = ["copy", "paste", "plain", "open", "locate", "pin", "delete"],
            }.Save(path);

            var loaded = AppSettings.Load(path);

            Assert.Equal(HoverActions.All, loaded.BarActions);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
            catch (IOException) { }
        }
    }

    [Fact]
    public void A_customised_action_list_survives_loading_untouched()
    {
        var path = Path.Combine(Path.GetTempPath(), "shiyu-actions", Guid.NewGuid().ToString("N"), "s.json");

        try
        {
            new AppSettings
            {
                BarActions = ["copy", "pin", "delete"],
            }.Save(path);

            var loaded = AppSettings.Load(path);

            // The user reordered and pruned; that is theirs to keep.
            Assert.Equal(["copy", "pin", "delete"], loaded.BarActions);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
            catch (IOException) { }
        }
    }
    // ── 「复制图中文字」（评审 2026-10-16 F-07）──

    [Fact]
    public void Copying_text_in_an_image_is_offered_only_for_images()
    {
        var images = HoverActions.AvailableFor(HoverActions.All, EntryKind.Image, hasOriginal: true);
        var text = HoverActions.AvailableFor(HoverActions.All, EntryKind.Text, hasOriginal: false);

        Assert.Contains("ocr", images);
        Assert.DoesNotContain("ocr", text);
    }

    [Fact]
    public void The_old_default_action_list_upgrades_to_include_the_new_action()
    {
        var old = new AppSettings { BarActions = HoverActions.FormerDefaultBeforeOcr };

        Assert.True(AppSettings.TryParse(
            System.Text.Json.JsonSerializer.Serialize(old), out var parsed));

        Assert.Equal(HoverActions.All, parsed.BarActions);
    }

    [Fact]
    public void A_hand_reordered_list_is_never_upgraded()
    {
        var reordered = HoverActions.FormerDefaultBeforeOcr.Reverse().ToArray();
        var kept = new AppSettings { BarActions = reordered };

        Assert.True(AppSettings.TryParse(
            System.Text.Json.JsonSerializer.Serialize(kept), out var parsed));

        Assert.Equal(reordered, parsed.BarActions);
    }
}
