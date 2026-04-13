using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;
using ReverseMarkdown;

namespace PlastBadgesParser
{
    public record BadgeDetail(
        string Id,
        string Title,
        string ImagePath,
        string Country,
        string Specialization,
        string Status,
        int? Level,
        string LastUpdated,
        string SeekerRequirements,
        string InstructorRequirements,
        List<string> FixNotes
    );

    public record ExportMetadata(
        string ParserVersion,
        string ToolAuthor,
        string ParserComment,
        string ParsedAtUtc,
        string SourceUrl,
        bool FixerEnabled,
        string FixerMode,
        int TotalBadges
    );

    public record BadgesExport(
        ExportMetadata Meta,
        List<BadgeDetail> Badges
    );

    public enum FixerMode
    {
        Soft,
        Off
    }

    public record ParserOptions(
        bool FixerEnabled,
        FixerMode FixerMode,
        bool ReportOnly,
        string InputPath,
        string LinkBaseUrl,
        string ReportOutputPath,
        string ProjectFilePath,
        string ImagesDirectoryPath,
        string ImagesPublicPrefix
    );

    class Program
    {
        // Parsed from https://plast.global/biblioteka-vmilostej/ - there are all badges in one page. / 12.04.2026
        private const string BaseUrl = "https://plast.global/biblioteka-vmilostej/";
        private const string BadgePublicBaseUrl = "https://plast.global/skills-library/";
        private const string OutputFileName = "badges.json";
        private const string DefaultImagesDirectoryName = "badges_images";
        private const string DefaultImagesPublicPrefix = "/badges_images/";
        private const string DefaultProjectFileName = "PlastBadgesParser.csproj";
        private const string ToolAuthor = "Rostyslav Mukha";
        private const string ParserVersionEnvVar = "PARSER_VERSION";
        private const string FallbackParserVersion = "1.0.0";
        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly Dictionary<DayOfWeek, string> DayPrefixes = new()
        {
            [DayOfWeek.Monday] = "пн",
            [DayOfWeek.Tuesday] = "вт",
            [DayOfWeek.Wednesday] = "ср",
            [DayOfWeek.Thursday] = "чт",
            [DayOfWeek.Friday] = "пт",
            [DayOfWeek.Saturday] = "сб",
            [DayOfWeek.Sunday] = "нд"
        };
        private static readonly Converter MarkdownConverter = new(new Config
        {
            GithubFlavored = true,
            RemoveComments = true
        });

        static async Task Main(string[] args)
        {
            var options = ParseOptions(args);
            var projectFilePath = ResolveProjectFilePath(options.ProjectFilePath);

            if (options.ReportOnly)
            {
                await RunReportOnlyAsync(options);
                return;
            }

            Console.WriteLine($"Починаємо збір даних... Fixer: {(options.FixerEnabled ? options.FixerMode.ToString() : "Off")}");

            string imagesDir = ResolveImagesDirectoryPath(options.ImagesDirectoryPath);
            if (!Directory.Exists(imagesDir))
            {
                Directory.CreateDirectory(imagesDir);
            }

            var imagesPublicPrefix = NormalizeImagesPublicPrefix(options.ImagesPublicPrefix);

            var badgesList = new List<BadgeDetail>();

            var detailLinks = await GetBadgeLinksAsync(BaseUrl);
            Console.WriteLine($"Знайдено {detailLinks.Count} вмілостей. Починаємо парсинг деталей...");

            foreach (var link in detailLinks)
            {
                try
                {
                    var badge = await ParseBadgeDetailsAsync(link, imagesDir, imagesPublicPrefix);
                    if (badge != null)
                    {
                        badgesList.Add(badge);
                        Console.WriteLine($"[+] Успішно спарсено: {badge.Title}");
                    }
                    
                    // Delay to be polite to the server and avoid potential rate limits.
                    // Tested with 0ms delay and it worked fine, but if you encounter issues, you can uncomment the line below to add a small delay between requests.
                    // await Task.Delay(500); 
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[x] Помилка при обробці {link}: {ex.Message}");
                }
            }

            if (options.FixerEnabled && options.FixerMode == FixerMode.Soft)
            {
                badgesList = badgesList.Select(ApplySoftFixes).ToList();
            }

            var parsedAtUtc = DateTime.UtcNow;
            var parserVersion = ResolveParserVersion(projectFilePath);
            var export = new BadgesExport(
                Meta: new ExportMetadata(
                    ParserVersion: parserVersion,
                    ToolAuthor: ToolAuthor,
                    ParserComment: $"PlastBadgesParser v{parserVersion}. Parsed from {BaseUrl} at {parsedAtUtc:yyyy-MM-dd HH:mm:ss} UTC",
                    ParsedAtUtc: parsedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                    SourceUrl: BaseUrl,
                    FixerEnabled: options.FixerEnabled,
                    FixerMode: options.FixerEnabled ? options.FixerMode.ToString().ToLowerInvariant() : "off",
                    TotalBadges: badgesList.Count
                ),
                Badges: badgesList
            );

            var jsonOptions = new JsonSerializerOptions 
            { 
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping // Cyrrillic characters support
            };
            
            string jsonString = JsonSerializer.Serialize(export, jsonOptions);
            await File.WriteAllTextAsync(OutputFileName, jsonString);

            Console.WriteLine($"\nГотово! Дані збережено у {OutputFileName}, а векторні зображення у папку {imagesDir}. Public image prefix: {imagesPublicPrefix}");
        }

        static string ResolveParserVersion(string projectFilePath)
        {
            var envVersion = ResolveParserVersionFromEnvironment();
            if (!string.IsNullOrWhiteSpace(envVersion))
            {
                return envVersion;
            }

            var projectVersion = ReadProjectVersion(projectFilePath);
            if (!string.IsNullOrWhiteSpace(projectVersion))
            {
                return projectVersion;
            }

            return GetAssemblyParserVersion();
        }

        static string GetAssemblyParserVersion()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var infoVersion = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(infoVersion))
            {
                // Trim source control suffixes such as "+commitSha" for cleaner metadata.
                return infoVersion.Split('+', 2)[0].Trim();
            }

            return assembly.GetName().Version?.ToString() ?? FallbackParserVersion;
        }

        static string ResolveParserVersionFromEnvironment()
        {
            var raw = Environment.GetEnvironmentVariable(ParserVersionEnvVar);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var version = raw.Trim();

            if (version.StartsWith("refs/tags/", StringComparison.OrdinalIgnoreCase))
            {
                version = version.Substring("refs/tags/".Length);
            }

            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                version = version.Substring(1);
            }

            // Accept semver with optional prerelease/build metadata.
            var semverMatch = Regex.Match(version, @"^(?<version>\d+\.\d+\.\d+(?:[-+][0-9A-Za-z\.-]+)?)$");
            if (!semverMatch.Success)
            {
                return string.Empty;
            }

            return semverMatch.Groups["version"].Value;
        }

        static string ResolveProjectFilePath(string cliProjectFilePath)
        {
            if (!string.IsNullOrWhiteSpace(cliProjectFilePath))
            {
                var explicitPath = Path.GetFullPath(cliProjectFilePath);
                if (!File.Exists(explicitPath))
                {
                    throw new FileNotFoundException($"Project file not found: {explicitPath}");
                }

                return explicitPath;
            }

            var cwdDefault = Path.Combine(Directory.GetCurrentDirectory(), DefaultProjectFileName);
            if (File.Exists(cwdDefault))
            {
                return cwdDefault;
            }

            var firstCsproj = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(firstCsproj))
            {
                throw new FileNotFoundException("Project file was not found in current directory. Use --project-file=<path>.");
            }

            return firstCsproj;
        }

        static string ReadProjectVersion(string projectFilePath)
        {
            var xml = XDocument.Load(projectFilePath);
            var versionElement = xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "Version");
            return versionElement?.Value?.Trim() ?? string.Empty;
        }

        static ParserOptions ParseOptions(string[] args)
        {
            bool fixerEnabled = true;
            FixerMode fixerMode = FixerMode.Soft;
            bool reportOnly = false;
            string inputPath = OutputFileName;
            string linkBaseUrl = BadgePublicBaseUrl;
            string reportOutputPath = string.Empty;
            string projectFilePath = string.Empty;
            string imagesDirectoryPath = DefaultImagesDirectoryName;
            string imagesPublicPrefix = DefaultImagesPublicPrefix;

            foreach (var arg in args)
            {
                if (string.Equals(arg, "--report-only", StringComparison.OrdinalIgnoreCase))
                {
                    reportOnly = true;
                    continue;
                }

                if (string.Equals(arg, "--no-fixer", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "--fixer=off", StringComparison.OrdinalIgnoreCase))
                {
                    fixerEnabled = false;
                    fixerMode = FixerMode.Off;
                    continue;
                }

                if (string.Equals(arg, "--fixer=on", StringComparison.OrdinalIgnoreCase))
                {
                    fixerEnabled = true;
                    if (fixerMode == FixerMode.Off)
                    {
                        fixerMode = FixerMode.Soft;
                    }

                    continue;
                }

                if (arg.StartsWith("--fixer-mode=", StringComparison.OrdinalIgnoreCase))
                {
                    var value = arg.Split('=', 2).LastOrDefault()?.Trim().ToLowerInvariant();
                    if (value == "soft")
                    {
                        fixerEnabled = true;
                        fixerMode = FixerMode.Soft;
                    }
                    else if (value == "off")
                    {
                        fixerEnabled = false;
                        fixerMode = FixerMode.Off;
                    }

                    continue;
                }

                if (arg.StartsWith("--input=", StringComparison.OrdinalIgnoreCase))
                {
                    inputPath = arg.Split('=', 2).LastOrDefault()?.Trim() ?? OutputFileName;
                    continue;
                }

                if (arg.StartsWith("--link-base=", StringComparison.OrdinalIgnoreCase))
                {
                    linkBaseUrl = arg.Split('=', 2).LastOrDefault()?.Trim() ?? BadgePublicBaseUrl;
                    continue;
                }

                if (arg.StartsWith("--report-out=", StringComparison.OrdinalIgnoreCase))
                {
                    reportOutputPath = arg.Split('=', 2).LastOrDefault()?.Trim() ?? string.Empty;
                    continue;
                }

                if (arg.StartsWith("--project-file=", StringComparison.OrdinalIgnoreCase))
                {
                    projectFilePath = arg.Split('=', 2).LastOrDefault()?.Trim() ?? string.Empty;
                    continue;
                }

                if (arg.StartsWith("--images-dir=", StringComparison.OrdinalIgnoreCase))
                {
                    imagesDirectoryPath = arg.Split('=', 2).LastOrDefault()?.Trim() ?? DefaultImagesDirectoryName;
                    continue;
                }

                if (arg.StartsWith("--images-public-prefix=", StringComparison.OrdinalIgnoreCase))
                {
                    imagesPublicPrefix = arg.Split('=', 2).LastOrDefault()?.Trim() ?? DefaultImagesPublicPrefix;
                }
            }

            return new ParserOptions(
                fixerEnabled,
                fixerMode,
                reportOnly,
                inputPath,
                linkBaseUrl,
                reportOutputPath,
                projectFilePath,
                imagesDirectoryPath,
                imagesPublicPrefix);
        }

        static string ResolveImagesDirectoryPath(string imagesDirectoryPath)
        {
            var value = string.IsNullOrWhiteSpace(imagesDirectoryPath)
                ? DefaultImagesDirectoryName
                : imagesDirectoryPath.Trim();

            return Path.GetFullPath(value);
        }

        static string NormalizeImagesPublicPrefix(string imagesPublicPrefix)
        {
            var value = string.IsNullOrWhiteSpace(imagesPublicPrefix)
                ? DefaultImagesPublicPrefix
                : imagesPublicPrefix.Trim();

            value = value.Replace('\\', '/');
            if (!value.StartsWith('/'))
            {
                value = "/" + value;
            }

            if (!value.EndsWith('/'))
            {
                value += "/";
            }

            return value;
        }

        static async Task RunReportOnlyAsync(ParserOptions options)
        {
            Console.WriteLine($"Report-only режим: файл '{options.InputPath}', fixer: {(options.FixerEnabled ? options.FixerMode.ToString() : "Off")}");

            var badges = await LoadBadgesFromJsonAsync(options.InputPath);
            var analyzed = badges;
            if (options.FixerEnabled && options.FixerMode == FixerMode.Soft)
            {
                analyzed = badges.Select(ApplySoftFixes).ToList();
            }

            var flagged = analyzed
                .Where(b => b.FixNotes != null && b.FixNotes.Count > 0)
                .OrderByDescending(b => b.FixNotes.Count)
                .ThenBy(b => b.Id)
                .ToList();

            var fullInputPath = Path.GetFullPath(options.InputPath);
            var fileName = Path.GetFileName(fullInputPath);
            var lineMap = BuildBadgeIdLineMap(await File.ReadAllLinesAsync(fullInputPath));

            var report = new StringBuilder();
            report.AppendLine($"Fix report for {fullInputPath}");
            report.AppendLine($"Total badges: {analyzed.Count}");
            report.AppendLine($"Badges with FixNotes: {flagged.Count}");
            report.AppendLine();

            if (flagged.Count == 0)
            {
                report.AppendLine("No badges with FixNotes were found.");
            }
            else
            {
                foreach (var badge in flagged)
                {
                    var sourceLink = BuildBadgePublicLink(options.LinkBaseUrl, badge.Id);
                    var fileRef = lineMap.TryGetValue(badge.Id, out var line)
                        ? $"{fullInputPath}:{line}"
                        : $"{fileName}:?";

                    report.AppendLine($"- {badge.Id} | {badge.Title}");
                    report.AppendLine($"  json: {fileRef}");
                    report.AppendLine($"  source: {sourceLink}");
                    report.AppendLine($"  notes: {string.Join(" | ", badge.FixNotes)}");
                }
            }

            Console.WriteLine(report.ToString());

            if (!string.IsNullOrWhiteSpace(options.ReportOutputPath))
            {
                var reportPath = Path.GetFullPath(options.ReportOutputPath);
                await File.WriteAllTextAsync(reportPath, report.ToString());
                Console.WriteLine($"Report збережено у {reportPath}");
            }
        }

        static async Task<List<BadgeDetail>> LoadBadgesFromJsonAsync(string inputPath)
        {
            var fullInputPath = Path.GetFullPath(inputPath);
            if (!File.Exists(fullInputPath))
            {
                throw new FileNotFoundException($"Input file not found: {fullInputPath}");
            }

            var json = await File.ReadAllTextAsync(fullInputPath);
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var trimmed = json.TrimStart();
            List<BadgeDetail>? badges = null;
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
            {
                badges = JsonSerializer.Deserialize<List<BadgeDetail>>(json, options);
            }
            else if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                var export = JsonSerializer.Deserialize<BadgesExport>(json, options);
                badges = export?.Badges;
            }

            if (badges == null)
            {
                throw new InvalidDataException("Unsupported JSON format. Expected either badge array or { meta, badges } object.");
            }

            return badges.Select(NormalizeBadgeShape).ToList();
        }

        static BadgeDetail NormalizeBadgeShape(BadgeDetail badge)
        {
            return badge with
            {
                Id = badge.Id ?? string.Empty,
                Title = badge.Title ?? string.Empty,
                ImagePath = badge.ImagePath ?? string.Empty,
                Country = badge.Country ?? string.Empty,
                Specialization = badge.Specialization ?? string.Empty,
                Status = badge.Status ?? string.Empty,
                LastUpdated = badge.LastUpdated ?? string.Empty,
                SeekerRequirements = badge.SeekerRequirements ?? string.Empty,
                InstructorRequirements = badge.InstructorRequirements ?? string.Empty,
                FixNotes = badge.FixNotes ?? new List<string>()
            };
        }

        static Dictionary<string, int> BuildBadgeIdLineMap(string[] lines)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var idRegex = new Regex("\"Id\"\\s*:\\s*\"(?<id>[^\"]+)\"", RegexOptions.Compiled);
            for (int i = 0; i < lines.Length; i++)
            {
                var match = idRegex.Match(lines[i]);
                if (match.Success)
                {
                    var id = match.Groups["id"].Value;
                    if (!map.ContainsKey(id))
                    {
                        map[id] = i + 1;
                    }
                }
            }

            return map;
        }

        static string BuildBadgePublicLink(string baseUrl, string id)
        {
            var normalizedBase = string.IsNullOrWhiteSpace(baseUrl) ? BadgePublicBaseUrl : baseUrl.Trim();
            if (!normalizedBase.EndsWith('/'))
            {
                normalizedBase += "/";
            }

            return string.IsNullOrWhiteSpace(id)
                ? normalizedBase
                : $"{normalizedBase}{id}/";
        }

        static async Task<List<string>> GetBadgeLinksAsync(string url)
        {
            var links = new List<string>();
            var html = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var nodes = doc.DocumentNode.SelectNodes("//div[contains(@class, 'card-skills')]//a");
            if (nodes != null)
            {
                foreach (var node in nodes)
                {
                    var href = node.GetAttributeValue("href", "");
                    if (!string.IsNullOrEmpty(href))
                    {
                        links.Add(href);
                    }
                }
            }
            
            // If there will be pagination in the future, we can add logic to follow "Next" links here. For now, we just return the unique links found on the first page.
            return links.Distinct().ToList();
        }

        static async Task<BadgeDetail?> ParseBadgeDetailsAsync(string url, string imagesDir, string imagesPublicPrefix)
        {
            var html = await _httpClient.GetStringAsync(url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var root = doc.DocumentNode;

            var title = root.SelectSingleNode("//h1[@id='title']")?.InnerText.Trim();
            if (string.IsNullOrEmpty(title)) return null;

            // Generate ID from URL (e.g., https://plast.global/skills-library/terenovi-igry-ta-zmagy-i/ -> terenovi-igry-ta-zmagy-i)
            var id = url.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;

            var country = ExtractSkillItem(root, "Країна:");
            var specialization = ExtractSkillItem(root, "Спеціалізація:");
            var status = ExtractSkillItem(root, "Статус вмілості:");
            var lastUpdated = ExtractSkillItem(root, "Остання зміна до інформації:");
            
            var levelStr = root.SelectSingleNode("//span[@class='skills-level']")?.InnerText.Trim();
            int? level = int.TryParse(levelStr, out int parsedLevel) ? parsedLevel : null;

            var seekerReqs = root.SelectSingleNode("//h3[contains(text(), 'Вимоги до здобувача')]/following-sibling::div[contains(@class, 'cms-editor')]")?.InnerHtml.Trim();
            var instructorReqs = root.SelectSingleNode("//h3[contains(text(), 'Вимоги до інструктора')]/following-sibling::div[contains(@class, 'cms-editor')]")?.InnerHtml.Trim();

            var imageUrl = root.SelectSingleNode("//img[@id='image']")?.GetAttributeValue("data-lazy-src", "");
            string localImagePath = "";

            if (!string.IsNullOrEmpty(imageUrl))
            {
                var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);
                localImagePath = Path.Combine(imagesDir, fileName);
                
                if (!File.Exists(localImagePath))
                {
                    var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl);
                    await File.WriteAllBytesAsync(localImagePath, imageBytes);
                }
                
                localImagePath = imagesPublicPrefix + fileName;
            }

            return new BadgeDetail(
                Id: id,
                Title: title,
                ImagePath: localImagePath,
                Country: country ?? string.Empty,
                Specialization: specialization ?? string.Empty,
                Status: status ?? string.Empty,
                Level: level,
                LastUpdated: lastUpdated ?? string.Empty,
                SeekerRequirements: seekerReqs ?? string.Empty,
                InstructorRequirements: instructorReqs ?? string.Empty,
                FixNotes: new List<string>()
            );
        }

        static BadgeDetail ApplySoftFixes(BadgeDetail badge)
        {
            var notes = new List<string>(badge.FixNotes ?? new List<string>());

            var fixedTitle = NormalizePlainText(badge.Title);
            var fixedCountry = NormalizePlainText(badge.Country);
            var fixedSpecialization = NormalizePlainText(badge.Specialization);
            var fixedStatus = NormalizeStatus(badge.Status, notes);
            var fixedLastUpdated = NormalizeLastUpdated(badge.LastUpdated, notes);
            var fixedImagePath = NormalizePlainText(badge.ImagePath);
            var fixedSeekerRequirements = ConvertRequirementsToMarkdown(badge.SeekerRequirements);
            var fixedInstructorRequirements = ConvertRequirementsToMarkdown(badge.InstructorRequirements);

            if (string.IsNullOrWhiteSpace(fixedCountry))
            {
                notes.Add("Country is empty. Manual review required.");
            }

            if (string.IsNullOrWhiteSpace(fixedSpecialization))
            {
                notes.Add("Specialization is empty. Left unchanged for manual clarification.");
            }

            if (string.IsNullOrWhiteSpace(fixedImagePath))
            {
                notes.Add("ImagePath is empty. Source does not provide image path.");
            }

            return badge with
            {
                Title = fixedTitle,
                Country = fixedCountry,
                Specialization = fixedSpecialization,
                Status = fixedStatus,
                LastUpdated = fixedLastUpdated,
                ImagePath = fixedImagePath,
                SeekerRequirements = fixedSeekerRequirements,
                InstructorRequirements = fixedInstructorRequirements,
                FixNotes = notes.Distinct().ToList()
            };
        }

        static string NormalizeStatus(string status, List<string> notes)
        {
            var fixedStatus = NormalizePlainText(status);
            if (string.IsNullOrWhiteSpace(fixedStatus))
            {
                notes.Add("Status is empty. Left unchanged for manual clarification.");
                return string.Empty;
            }

            if (fixedStatus.Contains("Апробаційна", StringComparison.OrdinalIgnoreCase) &&
                fixedStatus.Contains("Затверджена", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("Status had combined values and was normalized to 'Затверджена'.");
                return "Затверджена";
            }

            return fixedStatus;
        }

        static string NormalizeLastUpdated(string lastUpdated, List<string> notes)
        {
            var value = NormalizePlainText(lastUpdated);
            if (string.IsNullOrWhiteSpace(value))
            {
                notes.Add("LastUpdated is empty. Left unchanged.");
                return string.Empty;
            }

            var dateMatch = Regex.Match(value, @"(?<date>\d{2}\.\d{2}\.\d{4})");
            if (!dateMatch.Success)
            {
                notes.Add($"LastUpdated has unexpected format '{value}'. Manual review required.");
                return value;
            }

            var datePart = dateMatch.Groups["date"].Value;
            if (!DateTime.TryParseExact(datePart, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                notes.Add($"LastUpdated date part '{datePart}' is invalid. Manual review required.");
                return value;
            }

            var expectedPrefix = DayPrefixes[parsedDate.DayOfWeek];
            var existingPrefix = ExtractDatePrefix(value);
            if (string.IsNullOrWhiteSpace(existingPrefix))
            {
                notes.Add($"LastUpdated day prefix was missing and set to '{expectedPrefix}'.");
            }
            else if (!string.Equals(existingPrefix, expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                notes.Add($"LastUpdated day prefix '{existingPrefix}' was corrected to '{expectedPrefix}'.");
            }

            return $"{expectedPrefix}, {parsedDate:dd.MM.yyyy}";
        }

        static string ExtractDatePrefix(string value)
        {
            var match = Regex.Match(value, @"^\s*(?<prefix>[^\d,]+?)\s*,");
            if (!match.Success)
            {
                return string.Empty;
            }

            return NormalizePlainText(match.Groups["prefix"].Value).ToLowerInvariant();
        }

        static string ConvertRequirementsToMarkdown(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var markdown = MarkdownConverter.Convert(html);
            markdown = HtmlEntity.DeEntitize(markdown ?? string.Empty);
            markdown = markdown.Replace('\u00A0', ' ');
            markdown = markdown.Replace("–", "-");
            markdown = markdown.Replace("\r\n", "\n").Replace("\r", "\n");

            var normalizedLines = markdown
                .Split('\n')
                .Select(line => line.TrimEnd())
                .ToList();

            var compacted = new List<string>();
            bool previousLineEmpty = false;
            foreach (var line in normalizedLines)
            {
                var isEmpty = string.IsNullOrWhiteSpace(line);
                if (isEmpty && previousLineEmpty)
                {
                    continue;
                }

                compacted.Add(line);
                previousLineEmpty = isEmpty;
            }

            return string.Join("\n", compacted).Trim();
        }

        static string NormalizePlainText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = HtmlEntity.DeEntitize(value);
            normalized = normalized.Replace('\u00A0', ' ');
            normalized = normalized.Replace("–", "-");
            normalized = Regex.Replace(normalized, @"\s+", " ");

            return normalized.Trim();
        }

        static string? ExtractSkillItem(HtmlNode root, string label)
        {
            var node = root.SelectSingleNode($"//div[contains(@class, 'skills-item') and span[contains(text(), '{label}')]]");
            if (node != null)
            {
                var spanNode = node.SelectSingleNode(".//span");
                if (spanNode != null)
                {
                    return node.InnerText.Replace(spanNode.InnerText, "").Trim();
                }
            }
            return null;
        }
    }
}