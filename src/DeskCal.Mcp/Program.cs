using System.Text;
using DeskCal.Core;
using DeskCal.Core.Calendars;
using DeskCal.Data;
using DeskCal.Mcp;

// deskcal-mcp: the Model Context Protocol server for M-DeskCal. An AI client (for example Claude Desktop) starts it as a
// child process and talks to it over stdin/stdout, so stdout carries nothing but protocol messages; notes go to stderr.
//
//   env  DESKCAL_MCP_TOKEN   the access token shown in M-DeskCal (tray menu > AI 연동)
//   arg  --db=<path>         database file (default: %AppData%\DeskCal\deskcal.db)

var utf8 = new UTF8Encoding(false);
Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });

static int Fail(string message, int code) { Console.Error.WriteLine("deskcal-mcp: " + message); return code; }

string dbPath = args.FirstOrDefault(a => a.StartsWith("--db=", StringComparison.Ordinal))?["--db=".Length..]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeskCal", "deskcal.db");
if (!File.Exists(dbPath)) return Fail($"데이터베이스를 찾을 수 없습니다: {dbPath} (M-DeskCal 을 먼저 한 번 실행하세요)", 2);

var db = new DeskCalDb(dbPath);
int version;
try { version = db.SchemaVersion(); }
catch (Exception ex) { return Fail("데이터베이스를 열 수 없습니다: " + ex.Message, 2); }
if (version < 5) return Fail("데이터베이스가 오래된 형식입니다. M-DeskCal 을 최신 버전으로 한 번 실행해 주세요.", 2);   // the app owns schema upgrades

var settings = new SettingsStore(db);
var access = new McpAccess(settings);
if (!access.Enabled) return Fail("AI 연동이 꺼져 있습니다. M-DeskCal 트레이 메뉴 > AI 연동 에서 켜세요.", 3);
if (!access.Verify(Environment.GetEnvironmentVariable("DESKCAL_MCP_TOKEN"))) return Fail("접근 토큰이 올바르지 않습니다 (환경 변수 DESKCAL_MCP_TOKEN).", 4);

var holidays = HolidayOverrideFile.Load(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "holidays.json"));
var days = new KoreanDays(holidays.Overrides);
var server = new McpServer(db, new SystemClock(), d => days.Holidays.IsDayOff(d));

using var input = new StreamReader(Console.OpenStandardInput(), utf8);
await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await server.RunAsync(input, output, cts.Token);
return 0;
