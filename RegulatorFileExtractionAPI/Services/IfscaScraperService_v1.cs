using HtmlAgilityPack;
using Newtonsoft.Json.Linq;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using RegulatorFileExtractionAPI.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RegulatorFileExtractionAPI.Services
{
    /// <summary>
    /// HTML-only implementation of the IFSCA scraper.
    /// Unlike IfscaScraperService, this implementation does not start Chrome
    /// and does not execute JavaScript/DataTables code.
    /// </summary>
    public class IfscaScraperService_v1
    {
        private const string IfscaUrl = "https://ifsca.gov.in/Legal/Index/ogGPf3wx5GE=";

        private const int RequestTimeoutSeconds = 300;

        private class DocumentInfo
        {
            public string FileName { get; set; }
            public string FilePublishDate { get; set; }
            public DateTime PublishedDate { get; set; }
            public string DownloadUrl { get; set; }
        }

        // Existing HTML-only entry point is retained for comparison.
        public async Task<List<FileExtractionModelFromURL>>GetLatestRegulationsAsync(APIRequest request)
        {
            if (request == null)
                throw new ArgumentNullException("request");

            var documents = new List<DocumentInfo>();
            using (var client = CreateHttpClient())
            {
                string html = await client.GetStringAsync(IfscaUrl);
                var page = new HtmlDocument();
                page.LoadHtml(html);
                ExtractDocuments(page, request.RegulatorKey, IfscaUrl, documents);
                return await CreateResultsAsync(documents, request, client);
            }
        }

        // Exact browser workflow from IfscaScraperService, with HAP parsing.
        public async Task<List<FileExtractionModelFromURL>>GetLatestRegulationsHybridAsync(APIRequest request)
        {
            ValidateRequest(request);
            var documents = new List<DocumentInfo>();

            var options = new ChromeOptions();
            options.AddArgument("--headless=new");
            options.AddArgument("--disable-gpu");
            options.AddArgument("--no-sandbox");

            string driverDirectory =
                System.Web.Hosting.HostingEnvironment.MapPath("~/bin")
                ?? AppDomain.CurrentDomain.BaseDirectory;

            var driverService = ChromeDriverService.CreateDefaultService(
                driverDirectory, "chromedriver.exe");
            driverService.HideCommandPromptWindow = true;

            using (var driver = new ChromeDriver(driverService, options))
            {
                var wait = new WebDriverWait(
                    driver, TimeSpan.FromSeconds(RequestTimeoutSeconds));

                driver.Navigate().GoToUrl(IfscaUrl);
                DismissCookieBanner(driver);

                var searchInput = wait.Until(d =>
                {
                    var element = d.FindElement(By.CssSelector(
                        "input[placeholder='Search Text by Title']"));
                    return element.Displayed && element.Enabled ? element : null;
                });

                searchInput.Clear();
                searchInput.SendKeys(request.RegulatorKey);

                var searchButton = wait.Until(d =>
                {
                    var element = d.FindElement(By.Id("searchButton"));
                    return element.Displayed && element.Enabled ? element : null;
                });

                searchButton.Click();

                wait.Until(d => d.FindElements(
                    By.CssSelector("table tbody tr")).Any());

                while (true)
                {
                    var currentPage = new HtmlDocument();
                    currentPage.LoadHtml(driver.PageSource);
                    ExtractDocuments(
                        currentPage,
                        request.RegulatorKey,
                        driver.Url,
                        documents);

                    var next = driver.FindElements(By.CssSelector(
                        ".dataTables_paginate .paginate_button.next"))
                        .FirstOrDefault();

                    if (next == null ||
                        (next.GetAttribute("class") ?? "")
                            .IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0)
                        break;

                    string previousFirstRow = driver.FindElements(
                        By.CssSelector("table tbody tr"))
                        .FirstOrDefault()?.Text ?? "";

                    bool clicked = false;
                    for (int attempt = 0; attempt < 3 && !clicked; attempt++)
                    {
                        try
                        {
                            DismissCookieBanner(driver);
                            next = driver.FindElements(By.CssSelector(
                                ".dataTables_paginate .paginate_button.next"))
                                .FirstOrDefault();

                            if (next == null)
                                break;

                            ((IJavaScriptExecutor)driver).ExecuteScript(
                                "arguments[0].scrollIntoView({block:'center'});",
                                next);
                            next.Click();
                            clicked = true;
                        }
                        catch (StaleElementReferenceException)
                        {
                            System.Threading.Thread.Sleep(250);
                        }
                        catch (ElementClickInterceptedException)
                        {
                            DismissCookieBanner(driver);
                            System.Threading.Thread.Sleep(250);
                        }
                    }

                    if (!clicked)
                        throw new InvalidOperationException(
                            "IFSCA pagination could not be clicked after three attempts.");

                    wait.Until(d =>
                    {
                        var rows = d.FindElements(
                            By.CssSelector("table tbody tr"));
                        return rows.Count > 0 && rows[0].Text != previousFirstRow;
                    });
                }
            }

            using (var client = CreateHttpClient())
                return await CreateResultsAsync(documents, request, client);
        }

        // AJAX implementation. The endpoint is discovered from the page's
        // DataTables configuration, then called with the search value.
        public async Task<List<FileExtractionModelFromURL>>GetLatestRegulationsFromAjaxAsync(APIRequest request)
        {
            ValidateRequest(request);
            using (var client = CreateHttpClient())
            {
                string pageHtml = await client.GetStringAsync(IfscaUrl);
                string endpoint = FindAjaxEndpoint(pageHtml);

                if (string.IsNullOrWhiteSpace(endpoint))
                    throw new InvalidOperationException(
                        "IFSCA DataTables AJAX endpoint was not found in the page HTML. " +
                        "Use GetLatestRegulationsHybridAsync instead or provide the endpoint.");

                endpoint = ToAbsoluteUrl(IfscaUrl, endpoint);
                string separator = endpoint.Contains("?") ? "&" : "?";
                string url = endpoint + separator +
                    "search[value]=" + Uri.EscapeDataString(request.RegulatorKey) +
                    "&search[regex]=false&start=0&length=10000";

                string response = await client.GetStringAsync(url);
                var documents = ParseAjaxResponse(response, request.RegulatorKey, endpoint);
                return await CreateResultsAsync(documents, request, client);
            }
        }

        public Task<List<FileExtractionModelFromURL>>
            GetLatestDocumentsAsync(APIRequest request)
        {
            return GetLatestRegulationsAsync(request);
        }

        private static void ValidateRequest(APIRequest request)
        {
            if (request == null)
                throw new ArgumentNullException("request");

            if (!string.Equals(request.RegulatorName, "ifsca",
                StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException(
                    "RegulatorName must be 'ifsca'.", "request");

            if (string.IsNullOrWhiteSpace(request.RegulatorKey))
                throw new ArgumentException(
                    "RegulatorKey is required.", "request");
        }

        private static async Task<List<FileExtractionModelFromURL>>
            CreateResultsAsync(List<DocumentInfo> documents,
                APIRequest request, HttpClient client)
        {
            TimeZoneInfo indiaTimeZone = TimeZoneInfo.FindSystemTimeZoneById(
                "India Standard Time");
            DateTime today = TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.UtcNow, indiaTimeZone).Date;

            var latestTwo = documents
                .Where(x => x.PublishedDate.Date <= today)
                .GroupBy(x => x.DownloadUrl)
                .Select(g => g.First())
                .OrderByDescending(x => x.PublishedDate)
                .Take(2)
                .ToList();

            var result = new List<FileExtractionModelFromURL>();
            foreach (var item in latestTwo)
            {
                result.Add(new FileExtractionModelFromURL
                {
                    FileId = null,
                    FilePublishDate = item.FilePublishDate,
                    FileName = item.FileName,
                    RegulatorName = request.RegulatorName,
                    RegulatorKey = request.RegulatorKey,
                    CircularName = null,
                    CircularKey = null,
                    FileAsBytes = await client.GetByteArrayAsync(item.DownloadUrl)
                });
            }

            return result;
        }

        private static void DismissCookieBanner(IWebDriver driver)
        {
            ((IJavaScriptExecutor)driver).ExecuteScript(
                "var b=document.getElementById('cookieConsentBanner');" +
                "if(b){b.classList.remove('show');b.style.display='none';}");
        }

        private static void ExtractDocuments(HtmlDocument document,
            string regulatorKey, string pageUrl, List<DocumentInfo> documents)
        {
            var rows = document.DocumentNode.SelectNodes("//table//tbody//tr");
            if (rows == null)
                return;

            foreach (var row in rows)
            {
                var cells = row.SelectNodes("./td");
                if (cells == null || cells.Count < 5)
                    continue;

                string dateText = CleanText(cells[1].InnerText);
                string title = CleanText(cells[2].InnerText);
                DateTime publishedDate;

                if (!DateTime.TryParseExact(dateText, "dd/MM/yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None,
                    out publishedDate))
                    continue;

                var link = cells[4].SelectSingleNode(".//a[@href]");
                string href = link?.GetAttributeValue("href", null);
                if (string.IsNullOrWhiteSpace(href))
                    continue;

                documents.Add(new DocumentInfo
                {
                    FileName = title,
                    FilePublishDate = dateText,
                    PublishedDate = publishedDate,
                    DownloadUrl = ToAbsoluteUrl(pageUrl, href)
                });
            }
        }

        private static List<DocumentInfo> ParseAjaxResponse(
            string response, string regulatorKey, string pageUrl)
        {
            var documents = new List<DocumentInfo>();
            if (response.TrimStart().StartsWith("<"))
            {
                var html = new HtmlDocument();
                html.LoadHtml(response);
                ExtractDocuments(html, regulatorKey, pageUrl, documents);
                return documents;
            }

            var root = JObject.Parse(response);
            var rows = root["data"] as JArray;
            if (rows == null)
                throw new InvalidOperationException(
                    "The IFSCA AJAX response did not contain a data array.");

            foreach (JObject row in rows.OfType<JObject>())
            {
                string date = (string)(row["FilePublishDate"] ?? row["publishDate"] ?? row["date"]);
                string title = (string)(row["FileName"] ?? row["title"] ?? row["name"]);
                string href = (string)(row["DownloadUrl"] ?? row["downloadUrl"] ?? row["url"]);
                DateTime parsed;

                if (!DateTime.TryParseExact(date, "dd/MM/yyyy",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed) ||
                    string.IsNullOrWhiteSpace(href))
                    continue;

                documents.Add(new DocumentInfo
                {
                    FileName = title,
                    FilePublishDate = date,
                    PublishedDate = parsed,
                    DownloadUrl = ToAbsoluteUrl(pageUrl, href)
                });
            }

            return documents;
        }

        private static string FindAjaxEndpoint(string html)
        {
            var match = Regex.Match(
                html ?? string.Empty,
                @"(?:ajax|url)\s*:\s*['""]([^'""]+)['""]",
                RegexOptions.IgnoreCase);

            return match.Success
                ? WebUtility.HtmlDecode(match.Groups[1].Value)
                : null;
        }

        private static string ToAbsoluteUrl(string currentPageUrl, string url)
        {
            Uri absolute;
            if (Uri.TryCreate(url, UriKind.Absolute, out absolute))
                return absolute.ToString();
            return new Uri(new Uri(currentPageUrl), url).ToString();
        }

        private static string CleanText(string value)
        {
            return WebUtility.HtmlDecode(value ?? string.Empty)
                .Replace("\u00A0", " ").Trim();
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(RequestTimeoutSeconds) };
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", "Mozilla/5.0 Chrome/157 Safari/537.36");
            return client;
        }
    }
}
