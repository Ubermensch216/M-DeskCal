using System.IO.Compression;
using DeskCal.Core;
using DeskCal.Core.Exchange;
using DeskCal.Data;

namespace DeskCal.Tests;

public class TimetableRepositoryTests : IDisposable
{
    private readonly DbFixture _f = new();
    public void Dispose() => _f.Dispose();
    private TimetableRepository Repo => new(_f.Db, _f.Clock);

    [Fact]
    public void Config_defaults_round_trip_and_are_clamped_when_loaded()
    {
        Assert.Equal(new TimetableConfig(), Repo.LoadConfig(_f.Settings));
        var c = new TimetableConfig { Periods = 8, FirstStart = new TimeOnly(8, 40), ClassMinutes = 45, BreakMinutes = 5, LunchAfter = 3, LunchMinutes = 50, ShowWeekend = true };
        Repo.SaveConfig(_f.Settings, c);
        Assert.Equal(c, Repo.LoadConfig(_f.Settings));

        _f.Settings.SetInt("timetable.periods", 500);
        _f.Settings.SetString("timetable.first_start", "banana");
        var loaded = Repo.LoadConfig(_f.Settings);
        Assert.Equal(12, loaded.Periods);
        Assert.Equal(new TimeOnly(9, 0), loaded.FirstStart);                    // unreadable time falls back to the default
    }

    [Fact]
    public void Squares_are_added_changed_and_emptied()
    {
        Repo.SetCell(DayOfWeek.Monday, 1, "  수학 ", "301호", "#74C0FC");
        Repo.SetCell(DayOfWeek.Friday, 7, "동아리");
        Assert.Equal([new TimetableCell(DayOfWeek.Monday, 1, "수학", "301호", "#74C0FC"), new TimetableCell(DayOfWeek.Friday, 7, "동아리", "", null)], Repo.GetCells());

        Repo.SetCell(DayOfWeek.Monday, 1, "영어", "", null);                      // replaces, no duplicate
        Assert.Equal("영어", Repo.GetCells().Single(c => c.Day == DayOfWeek.Monday).Subject);

        Repo.SetCell(DayOfWeek.Monday, 1, "  ", "");                               // blank = empty the square
        Assert.Single(Repo.GetCells());
        Repo.SetCell(DayOfWeek.Monday, 1, "", "");                                 // emptying an empty square is harmless
        Repo.Clear();
        Assert.Empty(Repo.GetCells());
    }

    [Fact]
    public void Sunday_is_stored_distinctly_from_other_days()
    {
        Repo.SetCell(DayOfWeek.Sunday, 1, "일요 수업");
        Repo.SetCell(DayOfWeek.Saturday, 1, "토요 수업");
        Assert.Equal(["일요 수업", "토요 수업"], Repo.GetCells().OrderBy(c => c.Day).Select(c => c.Subject));
    }
}

public class NoteRepositoryExtraTests : IDisposable
{
    private readonly DbFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void Editing_records_history_and_restoring_swaps_text_back()
    {
        var n = _f.Notes.Add("장보기", "우유");
        _f.Notes.Update(n.Id, "장보기", "우유, 빵");
        _f.Notes.Update(n.Id, "장보기 목록", "우유, 빵, 달걀");
        var h = _f.History.List(EntityNames.Note, n.Id);
        Assert.Equal(["우유, 빵", "우유"], h.Select(x => x.Content));

        var restored = _f.Notes.RestoreFromHistory(h[1].Id)!;
        Assert.Equal(("장보기 목록", "우유"), (restored.Title, restored.Content));          // only the text is restored, the title stays
        Assert.Equal(3, _f.History.List(EntityNames.Note, n.Id).Count);                    // the replaced text was kept too
    }

    [Fact]
    public void Restore_refuses_other_entities_and_deleted_notes()
    {
        var item = _f.Items.Add("2026-11-09", "a");
        _f.Items.UpdateContent(item.Id, "b");
        Assert.Null(_f.Notes.RestoreFromHistory(_f.History.List(EntityNames.Item, item.Id)[0].Id));
        Assert.Null(_f.Notes.RestoreFromHistory(9999));

        var n = _f.Notes.Add("t", "one");
        _f.Notes.Update(n.Id, "t", "two");
        var hid = _f.History.List(EntityNames.Note, n.Id)[0].Id;
        _f.Notes.SoftDelete(n.Id);
        Assert.Null(_f.Notes.RestoreFromHistory(hid));
    }

    [Fact]
    public void Colour_and_undelete()
    {
        var n = _f.Notes.Add("t", "c");
        Assert.True(_f.Notes.SetColor(n.Id, "#FFE066"));
        Assert.Equal("#FFE066", _f.Notes.Get(n.Id)!.Color);
        _f.Notes.SoftDelete(n.Id);
        Assert.False(_f.Notes.SetColor(n.Id, "#000000"));          // not while deleted
        Assert.Empty(_f.Notes.List());
        Assert.True(_f.Notes.Undelete(n.Id));
        Assert.Single(_f.Notes.List());
    }
}

public class XlsxFileTests : IDisposable
{
    private readonly DbFixture _f = new();
    public void Dispose() => _f.Dispose();

    [Fact]
    public void Excel_files_are_written_through_the_exchange_service_and_refused_on_import()
    {
        _f.Items.Add("2026-11-09", "보고서");
        _f.Events.Add("회의", "2026-11-09T10:00", "2026-11-09T11:00");
        var svc = new ExchangeService(_f.Db, _f.Clock);
        string path = _f.TempFile("out.xlsx");
        ExchangeFiles.WriteFile(path, ExchangeFormat.Xlsx, svc.Collect(), DateTimeOffset.Now, xlsx: new XlsxOptions(null, null, DayOfWeek.Monday, d => d.Day == 9 ? "테스트일" : null));

        using (var zip = ZipFile.OpenRead(path))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "xl/worksheets/sheet2.xml");            // list + November
            using var s = new StreamReader(zip.GetEntry("xl/worksheets/sheet2.xml")!.Open());
            var xml = s.ReadToEnd();
            Assert.Contains("테스트일", xml);
        }
        Assert.Equal(0x50, File.ReadAllBytes(path)[0]);                                           // "PK": a zip archive
        Assert.Throws<ExchangeFormatException>(() => ExchangeFiles.ReadFile(path));
        Assert.Equal(ExchangeFormat.Xlsx, ExchangeFiles.FormatFromPath("x.XLSX"));
    }
}

public class ContentLockStoreTests : IDisposable
{
    private readonly DbFixture _f = new();
    public void Dispose() => _f.Dispose();
    private ContentLockStore Store => new(_f.Settings);

    [Fact]
    public void No_password_until_one_is_set_and_wrong_or_empty_never_verifies()
    {
        Assert.False(Store.HasPassword);
        Assert.False(Store.Verify("anything"));                       // nothing to verify against: the caller decides what that means
        Store.SetPassword("열쇠1234");
        Assert.True(Store.HasPassword);
        Assert.True(Store.Verify("열쇠1234"));
        Assert.False(Store.Verify("열쇠1235"));
        Assert.False(Store.Verify(null));
        Assert.False(Store.Verify(""));
    }

    [Fact]
    public void The_password_itself_is_never_stored()
    {
        Store.SetPassword("super-secret-pw");
        var all = string.Join("|", _f.Settings.GetAll().Values);
        Assert.DoesNotContain("super-secret-pw", all);
        Assert.Contains("pbkdf2-sha256$", all);
    }

    [Fact]
    public void Changing_and_clearing()
    {
        Store.SetPassword("first-pw");
        Store.SetPassword("second-pw");
        Assert.False(Store.Verify("first-pw"));
        Assert.True(Store.Verify("second-pw"));
        Store.ClearPassword();
        Assert.False(Store.HasPassword);
        Assert.False(Store.Verify("second-pw"));
    }

    [Fact]
    public void Too_short_passwords_are_refused_and_the_old_one_stays()
    {
        Store.SetPassword("good-pw");
        Assert.Throws<ArgumentException>(() => Store.SetPassword("abc"));
        Assert.True(Store.Verify("good-pw"));
    }

    [Fact]
    public void Hidden_flag_survives_a_restart_independently_of_the_password()
    {
        Assert.False(Store.Hidden);
        Store.Hidden = true;
        Assert.True(new ContentLockStore(_f.Settings).Hidden);
        Store.Hidden = false;
        Assert.False(new ContentLockStore(_f.Settings).Hidden);
    }

    [Fact]
    public void Content_hotkey_is_part_of_the_shortcut_set_and_does_not_clash()
    {
        var p = new AppPreferences();
        Assert.Null(p.Validate());
        Assert.Equal("Ctrl+Alt+L", p.ToggleContent.Format());
        var store = new AppPreferencesStore(_f.Settings);
        store.Save(p.With(HotkeyAction.ToggleContent, new Hotkey(HotkeyMods.Win | HotkeyMods.Shift, 'L')));
        Assert.Equal("Shift+Win+L", store.Load().ToggleContent.Format());   // canonical order: Ctrl, Alt, Shift, Win
    }

    [Fact]
    public void Skin_settings_round_trip_through_the_board_store()
    {
        var store = new BoardSettingsStore(_f.Settings);
        store.Save(new BoardSettings { SkinFolder = @"D:\사진 모음", SkinFit = SkinFit.Tile, SkinRotateMinutes = 30 });
        var s = store.Load();
        Assert.Equal((@"D:\사진 모음", SkinFit.Tile, 30), (s.SkinFolder, s.SkinFit, s.SkinRotateMinutes));
        Assert.Equal("", new BoardSettingsStore(new SettingsStore(new DbFixture().Db)).Load().SkinFolder);
    }
}
