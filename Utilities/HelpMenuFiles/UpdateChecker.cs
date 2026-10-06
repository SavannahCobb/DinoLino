using Newtonsoft.Json.Linq;
using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;

namespace DinoLino
{
    internal static class UpdateChecker
    {
        private const string LatestReleaseUrl =
            "https://api.github.com/repos/SavannahCobb/DinoLino/releases/latest";

        private static readonly HttpClient HttpClient = CreateHttpClient();

        public static async Task<UpdateCheckResult> CheckAsync()
        {
            try
            {
                using (HttpResponseMessage response =
                    await HttpClient.GetAsync(LatestReleaseUrl))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return UpdateCheckResult.NoReleaseFound();
                    }

                    response.EnsureSuccessStatusCode();

                    string json = await response.Content.ReadAsStringAsync();
                    JObject release = JObject.Parse(json);

                    string tagName = (string)release["tag_name"];
                    string releasePageUrl = (string)release["html_url"];

                    if (String.IsNullOrWhiteSpace(tagName))
                    {
                        return UpdateCheckResult.InvalidRelease();
                    }

                    string latestVersionText = tagName.Trim()
                        .TrimStart('v', 'V');

                    Version latestVersion;

                    if (!Version.TryParse(latestVersionText, out latestVersion))
                    {
                        return UpdateCheckResult.InvalidRelease();
                    }

                    Assembly entryAssembly = Assembly.GetEntryAssembly();

                    Version currentVersion = entryAssembly != null
                        ? entryAssembly.GetName().Version
                        : new Version(0, 0, 0);

                    bool updateAvailable = latestVersion > currentVersion;

                    return UpdateCheckResult.Success(
                        currentVersion,
                        latestVersion,
                        updateAvailable,
                        releasePageUrl);
                }
            }
            catch (HttpRequestException)
            {
                return UpdateCheckResult.ConnectionFailed();
            }
            catch (TaskCanceledException)
            {
                return UpdateCheckResult.ConnectionFailed();
            }
            catch (Exception)
            {
                return UpdateCheckResult.InvalidRelease();
            }
        }

        private static HttpClient CreateHttpClient()
        {
            HttpClient client = new HttpClient();

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "DinoLino-UpdateChecker");

            client.DefaultRequestHeaders.Accept.ParseAdd(
                "application/vnd.github+json");

            client.Timeout = TimeSpan.FromSeconds(10);

            return client;
        }
    }

    internal sealed class UpdateCheckResult
    {
        public bool WasSuccessful { get; set; }

        public bool UpdateAvailable { get; set; }

        public Version CurrentVersion { get; set; }

        public Version LatestVersion { get; set; }

        public string ReleasePageUrl { get; set; }

        public string ErrorMessage { get; set; }

        public static UpdateCheckResult Success(
            Version currentVersion,
            Version latestVersion,
            bool updateAvailable,
            string releasePageUrl)
        {
            return new UpdateCheckResult
            {
                WasSuccessful = true,
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                UpdateAvailable = updateAvailable,
                ReleasePageUrl = releasePageUrl
            };
        }

        public static UpdateCheckResult NoReleaseFound()
        {
            return new UpdateCheckResult
            {
                WasSuccessful = false,
                ErrorMessage =
                    "No published GitHub release was found for DinoLino."
            };
        }

        public static UpdateCheckResult InvalidRelease()
        {
            return new UpdateCheckResult
            {
                WasSuccessful = false,
                ErrorMessage =
                    "DinoLino could not read the latest GitHub release."
            };
        }

        public static UpdateCheckResult ConnectionFailed()
        {
            return new UpdateCheckResult
            {
                WasSuccessful = false,
                ErrorMessage =
                    "DinoLino could not connect to GitHub to check for updates."
            };
        }
    }
}