using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;
using HtmlAgilityPack;
using RegulatorFileExtractionAPI.Models;

namespace RegulatorFileExtractionAPI.Services
{
    public class MauritiusRegulationsService
    {
        private const string BaseUrl = "https://www.fscmauritius.org";

        private const string RegulationsUrl = "https://www.fscmauritius.org/regulatory-framework/rules-and-regulations/regulations";

        private const int MaxFileSize = 25 * 1024 * 1024;

        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();

            client.Timeout = TimeSpan.FromSeconds(90);

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120.0 Safari/537.36");

            return client;
        }


        public async Task<List<FileExtractionModelFromURL>>GetLatestRegulationsAsync(APIRequest request)
        {
            //DateTime currentDate = DateTime.Today.Year;

            var allDocuments = new List<DocumentInfo>();

            string currentPageUrl = RegulationsUrl;

            var visitedPages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // STEP 1: Retrieve all pages
            //while (!string.IsNullOrWhiteSpace(currentPageUrl))
            //{
            //if (!visitedPages.Add(currentPageUrl))
            //    break;

            List<DocumentInfo> documents = new List<DocumentInfo>();

            while (documents == null || documents.Count == 0)
            {
                string html = await GetHtmlAsync(currentPageUrl);

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                documents = ExtractAllDocuments(doc, currentPageUrl);
            }

            //currentPageUrl = FindNextPage(doc, currentPageUrl);
            //}           

            allDocuments.AddRange(documents);
            // STEP 2: Remove duplicate download URLs
            allDocuments = allDocuments
                .GroupBy(x => x.DownloadUrl,
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();

            // STEP 3: Filter by RegulatorKey in document title
            var regulatorDocuments = allDocuments
                .Where(x =>
                    !string.IsNullOrWhiteSpace(x.FileName) &&
                    x.FileName.Contains(request.RegulatorKey))
                .ToList();

            // STEP 4: Extract publication date
            foreach (var item in regulatorDocuments)
            {
                item.PublishDate = ExtractPublishDate(item.FileName);
            }

            // STEP 5: Filter valid dates and exclude future documents
            var eligibleDocuments = regulatorDocuments
            .Select(x =>
            {
                DateTime publishDate = DateTime.MinValue;
                bool validDate = false;

                if (!string.IsNullOrWhiteSpace(x.PublishDate))
                {
                    string dateValue = x.PublishDate.Trim();

                    // Handle year format (e.g. 2013)
                    if (Regex.IsMatch(dateValue, @"^\d{4}$"))
                    {
                        if (int.TryParse(dateValue, out int year) &&
                            year >= 1900)
                        {
                            publishDate = new DateTime(year, 1, 1);
                            validDate = true;
                        }
                    }
                    else
                    {
                        // Handle complete date format
                        validDate = DateTime.TryParse(
                            dateValue,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.None,
                            out publishDate);
                    }
                }

                return new
                {
                    Document = x,
                    PublishDate = publishDate,
                    IsValid = validDate
                };
            })
            .Where(x =>
                x.IsValid &&
                x.PublishDate.Date <= DateTime.Today)
            .ToList();

            // STEP 6: Group by RegulatorKey
            // Select latest TWO documents from the group
            var latestTwoDocuments = eligibleDocuments.Count() > 1 ?
                eligibleDocuments
                .GroupBy(x => request.RegulatorKey,
                    StringComparer.OrdinalIgnoreCase)
                .SelectMany(group => group
                    .OrderByDescending(x => x.PublishDate)
                    .ThenByDescending(x => x.Document.FileName)
                    .Take(2))
                .Select(x => x.Document)
                .ToList() :
                eligibleDocuments.Select(x => x.Document).ToList();

            // STEP 7: Download selected documents
            var result = new List<FileExtractionModelFromURL>();

            foreach (var item in latestTwoDocuments)
            {
                try
                {
                    byte[] fileBytes = await DownloadFileAsync(
                        item.DownloadUrl);

                    if (fileBytes == null || fileBytes.Length == 0)
                        continue;

                    result.Add(new FileExtractionModelFromURL
                    {
                        FileId = GenerateFileId(item.DownloadUrl),

                        FilePublishDate = item.PublishDate,

                        FileName = item.FileName,

                        RegulatorName = request.RegulatorName,

                        RegulatorKey = request.RegulatorKey,

                        CircularName = null,

                        CircularKey = null,

                        FileAsBytes = fileBytes
                    });
                }
                catch (Exception ex)
                {
                    // Log failed download and continue
                    System.Diagnostics.Debug.WriteLine(
                        $"Download failed: {item.FileName}: {ex.Message}");
                }
            }

            return result;
        }

        private async Task<string> GetHtmlAsync(string url)
        {
            ValidateFscUrl(url);

            using (var response = await Client.GetAsync(url))
            {
                string html = await response.Content.ReadAsStringAsync();

                System.Diagnostics.Debug.WriteLine(
                    "HTTP Status: " + (int)response.StatusCode);

                System.Diagnostics.Debug.WriteLine(
                    "Response URL: " + response.RequestMessage.RequestUri);

                if (response.StatusCode ==
                    System.Net.HttpStatusCode.Forbidden)
                {
                    throw new Exception(
                        "FSC Mauritius blocked the request (403 Forbidden). " +
                        "Check website access restrictions or bot protection.");
                }

                response.EnsureSuccessStatusCode();

                return html;
            }
        }
        private List<DocumentInfo> ExtractAllDocuments(HtmlDocument doc, string pageUrl)
        {
            var result = new List<DocumentInfo>();

            var anchors = doc.DocumentNode.SelectNodes("//a[@href]");

            if (anchors == null)
                return result;

            foreach (var anchor in anchors)
            {
                string href = anchor.GetAttributeValue(
                    "href", "").Trim();

                if (string.IsNullOrWhiteSpace(href))
                    continue;

                if (!Regex.IsMatch(
                    href,
                    @"\.pdf(?:$|[?#])",
                    RegexOptions.IgnoreCase))
                {
                    continue;
                }

                string linkText = HttpUtility.HtmlDecode(
                    anchor.InnerText).Trim();

                string parentText = HttpUtility.HtmlDecode(
                    anchor.ParentNode?.InnerText ?? "").Trim();

                string fileName = GetDocumentTitle(
                    anchor, linkText, parentText);

                if (string.IsNullOrWhiteSpace(fileName))
                    continue;

                string downloadUrl;

                try
                {
                    downloadUrl = new Uri(
                        new Uri(pageUrl), href).AbsoluteUri;

                    ValidateFscUrl(downloadUrl);
                }
                catch
                {
                    continue;
                }

                result.Add(new DocumentInfo
                {
                    FileName = fileName,
                    DownloadUrl = downloadUrl,
                    HtmlContent = anchor.ParentNode?.OuterHtml ?? ""
                });
            }

            return result
                .GroupBy(x => x.DownloadUrl,
                    StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToList();
        }
        private string GetDocumentTitle(HtmlNode anchor, string linkText, string parentText)
        {
            if (!string.IsNullOrWhiteSpace(linkText) &&
                !Regex.IsMatch(
                    linkText,
                    @"^(download|view|pdf)$",
                    RegexOptions.IgnoreCase))
            {
                return CleanTitle(linkText);
            }

            HtmlNode node = anchor;

            for (int i = 0; i < 4 && node != null; i++)
            {
                string text = HttpUtility.HtmlDecode(
                    node.InnerText).Trim();

                text = Regex.Replace(
                    text,
                    @"download|view pdf",
                    "",
                    RegexOptions.IgnoreCase);

                text = CleanTitle(text);

                if (!string.IsNullOrWhiteSpace(text) &&
                    text.Length > 10)
                    return text;

                node = node.ParentNode;
            }

            return CleanTitle(parentText);
        }
        private string CleanTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return null;

            return Regex.Replace(title, @"\s+", " ").Trim();
        }

        private string FindNextPage(HtmlDocument doc, string currentUrl)
        {
            var next = doc.DocumentNode.SelectSingleNode(
                "//a[@rel='next'] | " +
                "//a[contains(@class,'next') or " +
                "contains(@class,'pagination-next')]");

            if (next == null)
                return null;

            string href = next.GetAttributeValue("href", "");

            if (string.IsNullOrWhiteSpace(href) ||
                href == "#" ||
                href.StartsWith(
                    "javascript:",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                string nextUrl = new Uri(
                    new Uri(currentUrl), href).AbsoluteUri;

                ValidateFscUrl(nextUrl);

                return nextUrl;
            }
            catch
            {
                return null;
            }
        }

        private string ExtractPublishDate(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return null;

            var datePatterns = new[]
            {
        @"\b\d{1,2}\s+[A-Za-z]+\s+\d{4}\b",
        @"\b\d{1,2}[/-]\d{1,2}[/-]\d{4}\b",
        @"\b\d{4}-\d{2}-\d{2}\b"
    };

            foreach (string pattern in datePatterns)
            {
                var match = Regex.Match(fileName, pattern);

                if (match.Success)
                {
                    DateTime date;

                    if (DateTime.TryParse(
                        match.Value,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out date))
                    {
                        return date.ToString("yyyy-MM-dd");
                    }
                }
            }

            var yearMatches = Regex.Matches(
                fileName, @"\b(?:19|20)\d{2}\b");

            if (yearMatches.Count > 0)
            {
                int year = int.Parse(
                    yearMatches[yearMatches.Count - 1].Value);

                return new DateTime(year, 1, 1).ToString("yyyy");
            }

            return null;
        }
        private async Task<byte[]> DownloadFileAsync(string url)
        {
            ValidateFscUrl(url);

            using (var response = await Client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();

                if (response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value > MaxFileSize)
                {
                    throw new Exception(
                        "Document exceeds maximum allowed size.");
                }

                byte[] bytes = await response.Content.ReadAsByteArrayAsync();

                if (bytes.Length > MaxFileSize)
                {
                    throw new Exception(
                        "Document exceeds maximum allowed size.");
                }

                return bytes;
            }
        }
        private void ValidateFscUrl(string url)
        {
            Uri uri;

            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(
                    uri.Host,
                    "www.fscmauritius.org",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new Exception("Invalid FSC document URL.");
            }
        }

        private string GenerateFileId(string url)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(url));

                return BitConverter.ToString(hash)
                    .Replace("-", "");
            }
        }
        private class DocumentInfo
        {
            public string FileName { get; set; }

            public string DownloadUrl { get; set; }

            public string PublishDate { get; set; }

            public string HtmlContent { get; set; }
        }
    }
}