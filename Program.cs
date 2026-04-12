using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        FixerMode FixerMode
    );

    class Program
    {
        // Parsed from https://plast.global/biblioteka-vmilostej/ - there are all badges in one page. / 12.04.2026
        private const string BaseUrl = "https://plast.global/biblioteka-vmilostej/";
        private const string OutputFileName = "badges.json";
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
            Console.WriteLine($"Починаємо збір даних... Fixer: {(options.FixerEnabled ? options.FixerMode.ToString() : "Off")}");

            string imagesDir = Path.Combine(Directory.GetCurrentDirectory(), "badges_images");
            if (!Directory.Exists(imagesDir))
            {
                Directory.CreateDirectory(imagesDir);
            }

            var badgesList = new List<BadgeDetail>();

            var detailLinks = await GetBadgeLinksAsync(BaseUrl);
            Console.WriteLine($"Знайдено {detailLinks.Count} вмілостей. Починаємо парсинг деталей...");

            foreach (var link in detailLinks)
            {
                try
                {
                    var badge = await ParseBadgeDetailsAsync(link, imagesDir);
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
            var export = new BadgesExport(
                Meta: new ExportMetadata(
                    ParserComment: $"Parsed from {BaseUrl} at {parsedAtUtc:yyyy-MM-dd HH:mm:ss} UTC",
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

            Console.WriteLine($"\nГотово! Дані збережено у {OutputFileName}, а векторні зображення у папку {imagesDir}");
        }

        static ParserOptions ParseOptions(string[] args)
        {
            bool fixerEnabled = true;
            FixerMode fixerMode = FixerMode.Soft;

            foreach (var arg in args)
            {
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
                }
            }

            return new ParserOptions(fixerEnabled, fixerMode);
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

        static async Task<BadgeDetail?> ParseBadgeDetailsAsync(string url, string imagesDir)
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
                
                localImagePath = $"/images/badges/{fileName}";
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