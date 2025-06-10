﻿using Cocona;
using System.Text.Json;          // JSON 파싱
using System.IO;                 // File, Path
using System.Linq;

// ───────────────────────────────────────────────────────
// ① 캐시 시뮬레이션 상수 (현재는 항상 Disabled)
// ───────────────────────────────────────────────────────
const bool CACHE_ENABLED = false;

CoconaApp.Run((
    [Argument(Description = "분석할 저장소. \"owner/repo\" 형식으로 공백을 구분자로 하여 여러 개 입력")] string[] repos,
    [Option('v', Description = "자세한 로그 출력을 활성화합니다.")] bool verbose,
    [Option('o', Description = "출력 디렉토리 경로를 지정합니다. (default : \"result\")", ValueName = "Output directory")] string? output,
    [Option('f', Description = "출력 형식 지정 (\"text\", \"csv\", \"chart\", \"html\", \"all\", default : \"all\")", ValueName = "Output format")] string[]? format,
    [Option('t', Description = "GitHub 액세스 토큰 입력", ValueName = "Github token")] string? token,
    [Option("include-user", Description = "결과에 포함할 사용자 ID 목록", ValueName = "Include user's id")] string[]? includeUsers,
    [Option("since", Description = "이 날짜 이후의 PR 및 이슈만 분석 (YYYY-MM-DD)", ValueName = "Start date")] string? since,
    [Option("until", Description = "이 날짜까지의 PR 및 이슈만 분석 (YYYY-MM-DD)", ValueName = "End date")] string? until,
    [Option("user-info", Description = "ID→이름 매핑 JSON/CSV 파일 경로")] string? userInfoPath
) =>
{
    // ───────────────────────────────────────────────────────
    // A) user-info 옵션으로 전달된 JSON/CSV 파일을 파싱해서 idToNameMap에 저장
    // ───────────────────────────────────────────────────────
    Dictionary<string,string>? idToNameMap = null;
    if (!string.IsNullOrWhiteSpace(userInfoPath))
    {
        var ext = Path.GetExtension(userInfoPath).ToLowerInvariant();
        try
        {
            if (ext == ".json")
            {
                var json = File.ReadAllText(userInfoPath);
                idToNameMap = JsonSerializer.Deserialize<Dictionary<string,string>>(json);
            }
            else if (ext == ".csv")
            {
                idToNameMap = File.ReadAllLines(userInfoPath)
                    .Skip(1) // 헤더(Id,Name) 스킵
                    .Select(line => line.Split(','))
                    .Where(parts => parts.Length == 2)
                    .ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                Console.WriteLine("올바르지 못한 포멧입니다.");
                return;
            }
            if (idToNameMap == null || idToNameMap.Count == 0)
                throw new Exception();
        }
        catch
        {
            Console.WriteLine("올바르지 못한 포멧입니다.");
            return;
        }
    }

    // ───────────────────────────────────────────────────────
    // 1) output 옵션 누락 시 기본값 안내
    // ───────────────────────────────────────────────────────
    if (string.IsNullOrWhiteSpace(output))
    {
        // 실제 디폴트 값은 코드에서 "output"으로 설정되어 있음
        Console.WriteLine("출력 디렉토리가 지정되지 않아 기본 경로 'output/'이 사용됩니다.");
    }

    // ───────────────────────────────────────────────────────
    // 2) format 옵션 누락 시 기본값 안내
    // ───────────────────────────────────────────────────────
    if (format == null || format.Length == 0)
    {
        // 여기서 기본값 배열은 {"text", "csv", "chart", "html"}으로 설정됨
        Console.WriteLine("출력 형식이 지정되지 않아 기본값 'all'이 사용됩니다.");
    }

    // 저장소별 라벨 통계 요약 정보를 저장할 리스트
    var summaries = new List<(string RepoName, Dictionary<string, int> LabelCounts)>();
    var failedRepos = new List<string>(); // ❗ 실패한 저장소 목록 수집용
    var totalScores = new Dictionary<string, UserScore>();

    // _client 초기화 
    RepoDataCollector.CreateClient(token);

    foreach (var repoPath in repos)
    {
        // repoPath 파싱 및 형식 검사  
        var parsed = TryParseRepoPath(repoPath);
        if (parsed == null)
        {
            failedRepos.Add(repoPath);
            continue; // 형식 오류는 건너뜀
        }

        var (owner, repo) = parsed.Value;

        // collector 생성
        var collector = new RepoDataCollector(owner, repo);

        // 데이터 수집
        var userActivities = collector.Collect(since: since, until: until);

        Console.WriteLine($"\n🔍 처리 중: {owner}/{repo}");

        try
        {
            // 테스트 출력, 라벨 카운트 기능 유지
            Dictionary<string, int> labelCounts = new Dictionary<string, int>
            {
                { "bug", 0 },
                { "documentation", 0 },
                { "typo", 0 }
            };
            string filePath = Path.Combine("output", repo, $"{repo}2.txt");
            string directoryPath = Path.GetDirectoryName(filePath) ?? throw new InvalidOperationException($"Invalid file path: {filePath}");
            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
            using (var writer = new StreamWriter(filePath))
            {
                writer.WriteLine($"=== {repo} Activities ===");
                HashSet<string>? userSet = null;
                if (includeUsers != null && includeUsers.Length > 0)
                    userSet = new HashSet<string>(includeUsers, StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in userActivities)
                {
                    string userId = kvp.Key;
                    UserActivity activity = kvp.Value;

                    if (userSet != null && !userSet.Contains(userId))
                        continue;

                    writer.WriteLine($"User ID: {userId}");
                    writer.WriteLine($"  PR_fb: {activity.PR_fb}");
                    writer.WriteLine($"  PR_doc: {activity.PR_doc}");
                    writer.WriteLine($"  PR_typo: {activity.PR_typo}");
                    writer.WriteLine($"  IS_fb: {activity.IS_fb}");
                    writer.WriteLine($"  IS_doc: {activity.IS_doc}");
                    writer.WriteLine(); // 빈 줄

                    // 라벨 카운트
                    labelCounts["bug"] += activity.PR_fb + activity.IS_fb;
                    labelCounts["documentation"] += activity.PR_doc + activity.IS_doc;
                    labelCounts["typo"] += activity.PR_typo;
                }
            }
            summaries.Add(($"{owner}/{repo}", labelCounts));

            // 존재하지 않는 사용자에 대한 경고 메시지 출력
            if (includeUsers != null && includeUsers.Length > 0)
            {
                var existingUsers = userActivities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var missingUsers = includeUsers.Where(u => !existingUsers.Contains(u)).ToList();
                if (missingUsers.Any())
                {
                    Console.WriteLine($"⚠️ 다음 사용자는 {owner}/{repo} 저장소에서 찾을 수 없습니다: {string.Join(", ", missingUsers)}\n");
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"! 오류 발생: {e.Message}");
            continue;
        }

        try
        {
            // ───────────────────────────────────────────────────────
            // 3) 실제 format 기본값/유효성 검사 적용
            // ───────────────────────────────────────────────────────
            List<string> formats;
            if (format == null || format.Length == 0)
            {
                formats = new List<string> { "text", "csv", "chart", "html" };
            }
            else
            {
                formats = checkFormat(format);
            }

            // ───────────────────────────────────────────────────────
            // 4) 실제 outputDir 기본값 적용
            // ───────────────────────────────────────────────────────
            string outputDir = string.IsNullOrWhiteSpace(output) ? "output" : output;

            // C) ID→이름 치환: userInfoPath가 주어졌으면 매핑, 아니면 원래 ID 유지
            var rawScores = userActivities.ToDictionary(pair => pair.Key, pair => ScoreAnalyzer.FromActivity(pair.Value));
            var finalScores = idToNameMap != null
                ? rawScores.ToDictionary(
                    kvp => idToNameMap.TryGetValue(kvp.Key, out var name) ? name : kvp.Key,
                    kvp => kvp.Value,
                    StringComparer.OrdinalIgnoreCase)
                : rawScores;

            var generator = new FileGenerator(finalScores, repo, outputDir);

            if (formats.Contains("csv"))
            {
                generator.GenerateCsv();
            }
            if (formats.Contains("text"))
            {
                generator.GenerateTable();
            }
            if (formats.Contains("chart"))
            {
                generator.GenerateChart();
            }
            if (formats.Contains("html"))
            {
                Console.WriteLine("html 파일 생성이 아직 구현되지 않았습니다.");
            }
             // 👉 totalScores에 병합
            foreach (var (user, score) in finalScores)
            {
                if (!totalScores.ContainsKey(user))
                    totalScores[user] = score;
                else
                {
                    var existing = totalScores[user];
                    totalScores[user] = new UserScore(
                        existing.PR_fb + score.PR_fb,
                        existing.PR_doc + score.PR_doc,
                        existing.PR_typo + score.PR_typo,
                        existing.IS_fb + score.IS_fb,
                        existing.IS_doc + score.IS_doc,
                        existing.total + score.total
                    );
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"! 오류 발생: {ex.Message}");
        }
    }
    // 👉 total.txt 출력
    if (totalScores.Count > 0)
    {
        string totalOutputDir = string.IsNullOrWhiteSpace(output) ? "output" : output;
        string totalPath = Path.Combine(totalOutputDir, "total.txt");
        var totalGen = new FileGenerator(totalScores, "total", totalOutputDir);
        totalGen.GenerateTotalText(totalPath); // 이 메서드는 다음 단계에서 정의합니다.
    }
    // 전체 저장소 요약 테이블 출력
    if (summaries.Count > 0)
    {
        Console.WriteLine("\n📊 전체 저장소 요약 통계");
        Console.WriteLine("----------------------------------------------------");
        Console.WriteLine($"{"Repo",-30} {"B/F",5} {"Doc",5} {"typo",5}");
        Console.WriteLine("----------------------------------------------------");

        foreach (var (repoName, counts) in summaries)
        {
            Console.WriteLine($"{repoName,-30} {counts["bug"],5} {counts["documentation"],5} {counts["typo"],5}");
        }
    }

    // ❗ 실패 저장소 요약 출력
    if (failedRepos.Count > 0)
    {
        Console.WriteLine("\n❌ 처리되지 않은 저장소 목록:");
        foreach (var r in failedRepos)
        {
            Console.WriteLine($"- {r} (올바른 형식: owner/repo)");
        }
    }
});

static List<string> checkFormat(string[] format)
{
    var FormatList = new List<string> { "text", "csv", "chart", "html", "all" }; // 유효한 format

    var validFormats = new List<string> { };
    var unValidFormats = new List<string> { };
    char[] invalidChars = Path.GetInvalidFileNameChars();

    foreach (var fm in format)
    {
        var f = fm.Trim().ToLowerInvariant(); // 대소문자 구분 없이 유효성 검사
        if (f.IndexOfAny(invalidChars) >= 0)
        {
            Console.WriteLine($"포맷 '{f}'에는 파일명으로 사용할 수 없는 문자가 포함되어 있습니다.");
            Console.WriteLine("포맷 이름에서 다음 문자를 사용하지 마세요: " +
                string.Join(" ", invalidChars.Select(c => $"'{c}'")));
            Environment.Exit(1);
        }

        if (FormatList.Contains(f))
            validFormats.Add(f);
        else
            unValidFormats.Add(f);
    }

    // 유효하지 않은 포맷이 존재
    if (unValidFormats.Count != 0)
    {
        Console.WriteLine("유효하지 않은 포맷이 존재합니다.");
        Console.Write("유효하지 않은 포맷: ");
        foreach (var unValidFormat in unValidFormats)
        {
            Console.Write($"{unValidFormat} ");
        }
        Console.Write("\n");
        Environment.Exit(1);
    }

    // 추출한 리스트에 "all"이 존재할 경우 모든 포맷 리스트 반환
    if (validFormats.Contains("all"))
    {
        return new List<string> { "text", "csv", "chart", "html" };
    }

    return validFormats;
}

static (string, string)? TryParseRepoPath(string repoPath)
{
    var parts = repoPath.Split('/');
    if (parts.Length != 2)
    {
        Console.WriteLine($"⚠️ 저장소 인자 '{repoPath}'는 'owner/repo' 형식이어야 합니다. (예: oss2025hnu/reposcore-cs");
        return null;
    }

    return (parts[0], parts[1]);
}
