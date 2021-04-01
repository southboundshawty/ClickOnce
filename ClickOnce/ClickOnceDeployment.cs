
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ClickOnceCore
{
    public class ClickOnceDeployment
    {
        private readonly bool _isNetworkDeployment;

        private readonly string _currentAppName;
        private readonly string _currentPath;
        private readonly string _publishPath;
        private readonly string _dataDir;

        private InstallFrom _from;

        public bool IsNetworkDeployment => _isNetworkDeployment;

        public string DataDir => _dataDir;

        public ClickOnceDeployment(string publishPath)
        {
            _publishPath = publishPath;
            _currentPath = AppDomain.CurrentDomain.SetupInformation.ApplicationBase;
            _isNetworkDeployment = CheckIsNetworkDeployment();
            _currentAppName = Assembly.GetEntryAssembly()?.GetName().Name;

            if (string.IsNullOrEmpty(_currentAppName))
            {
                throw new ClickOnceDeploymentException("Can't find entry assembly name!");
            }

            if (_isNetworkDeployment && !string.IsNullOrEmpty(_currentPath))
            {
                string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string programData = Path.Combine(localData, @"Apps\2.0\Data\");
                string currentFolderName = new DirectoryInfo(_currentPath).Name;

                _dataDir = SearchAppDataDir(programData, currentFolderName, 0);
            }
            else
            {
                _dataDir = string.Empty;
            }

            SetInstallFrom();
        }

        private string SearchAppDataDir(string programData, string currentFolderName, int i)
        {
            i++;

            if (i > 100)
            {
                throw new ClickOnceDeploymentException($"Can't find data dir for {currentFolderName} in path: {programData}");
            }

            string[] subdirectoryEntries = Directory.GetDirectories(programData);
            string result = string.Empty;

            foreach (string dir in subdirectoryEntries)
            {
                if (dir.Contains(currentFolderName))
                {
                    result = Path.Combine(dir, "Data");
                    break;
                }

                result = SearchAppDataDir(Path.Combine(programData, dir), currentFolderName, i);

                if (!string.IsNullOrEmpty(result))
                {
                    break;
                }
            }

            return result;
        }

        private void SetInstallFrom()
        {
            if (_isNetworkDeployment && !string.IsNullOrEmpty(_publishPath))
            {
                _from = _publishPath.StartsWith("http") ? InstallFrom.Web : InstallFrom.Unc;
            }
            else
            {
                _from = InstallFrom.NoNetwork;
            }
        }

        public async Task<Version> CurrentVersion()
        {
            if (!IsNetworkDeployment)
            {
                throw new ClickOnceDeploymentException("Not deployed by network!");
            }

            if (string.IsNullOrEmpty(_currentAppName))
            {
                throw new ClickOnceDeploymentException("Application name is empty!");
            }

            string path = Path.Combine(_currentPath, $"{_currentAppName}.exe.manifest");

            if (!File.Exists(path))
            {
                throw new ClickOnceDeploymentException($"Can't find manifest file at path {path}");
            }

            string fileContent = await File.ReadAllTextAsync(path);

            XDocument xmlDoc = XDocument.Parse(fileContent, LoadOptions.None);
            XNamespace nsSys = "urn:schemas-microsoft-com:asm.v1";
            XElement xmlElement = xmlDoc.Descendants(nsSys + "assemblyIdentity").FirstOrDefault();

            if (xmlElement == null)
            {
                throw new ClickOnceDeploymentException($"Invalid manifest document for {path}");
            }

            string version = xmlElement.Attribute("version")?.Value;

            if (string.IsNullOrEmpty(version))
            {
                throw new ClickOnceDeploymentException("Version info is empty!");
            }

            return new Version(version);
        }

        public async Task<Version> ServerVersion()
        {
            if (_from == InstallFrom.Web)
            {
                using HttpClient client = new HttpClient { BaseAddress = new Uri(_publishPath) };

                using Stream stream = await client.GetStreamAsync($"{_currentAppName}.application");

                return await ReadServerManifest(stream);
            }

            if (_from == InstallFrom.Unc)
            {
                using FileStream stream = File.OpenRead(Path.Combine($"{_publishPath}", $"{_currentAppName}.application"));

                return await ReadServerManifest(stream);
            }

            throw new ClickOnceDeploymentException("No network install was set");
        }

        private async Task<Version> ReadServerManifest(Stream stream)
        {
            XDocument xmlDoc = await XDocument.LoadAsync(stream, LoadOptions.None, CancellationToken.None);
            XNamespace nsSys = "urn:schemas-microsoft-com:asm.v1";
            XElement xmlElement = xmlDoc.Descendants(nsSys + "assemblyIdentity").FirstOrDefault();

            if (xmlElement == null)
            {
                throw new ClickOnceDeploymentException($"Invalid manifest document for {_currentAppName}.application");
            }

            string version = xmlElement.Attribute("version")?.Value;

            if (string.IsNullOrEmpty(version))
            {
                throw new ClickOnceDeploymentException($"Version info is empty!");
            }

            return new Version(version);
        }

        public async Task<bool> UpdateAvailable()
        {
            Version currentVersion = await CurrentVersion();
            Version serverVersion = await ServerVersion();

            return currentVersion < serverVersion;
        }

        public async Task<bool> Update()
        {
            Version currentVersion = await CurrentVersion();
            Version serverVersion = await ServerVersion();

            if (currentVersion >= serverVersion)
            {
                // Nothing to update
                return false;
            }

            Process proc;

            string setupPath = null;

            if (_from == InstallFrom.Web)
            {
                string downLoadFolder = Environment.ExpandEnvironmentVariables("%userprofile%\\downloads\\");

                string setupFileName = $"{Guid.NewGuid()}setup{serverVersion}.exe";

                Uri uri = new Uri($"{_publishPath}setup.exe");

                setupPath = Path.Combine(downLoadFolder, setupFileName);

                using (HttpClient client = new HttpClient())
                {
                    HttpResponseMessage response = await client.GetAsync(uri);

                    using FileStream fs = new FileStream(setupPath, FileMode.CreateNew);

                    await response.Content.CopyToAsync(fs);
                }

                proc = OpenUrl(setupPath);
            }
            else if (_from == InstallFrom.Unc)
            {
                proc = OpenUrl(Path.Combine($"{_publishPath}", $"{_currentAppName}.application"));
            }
            else
            {
                throw new ClickOnceDeploymentException("No network install was set");
            }

            if (proc == null)
            {
                throw new ClickOnceDeploymentException("Can't start update process");
            }

            await proc.WaitForExitAsync();

            if (!string.IsNullOrEmpty(setupPath))
            {
                File.Delete(setupPath);
            }

            return true;
        }

        private static Process OpenUrl(string url)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(url)
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    UseShellExecute = false
                };

                Process proc = Process.Start(info);

                return proc;
            }
            catch
            {
                // hack because of this: https://github.com/dotnet/corefx/issues/10361
                url = url.Replace("&", "^&");

                return Process.Start(new ProcessStartInfo("cmd", $"/c start {url}")
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = false,
                    UseShellExecute = false,
                });
            }
        }

        private bool CheckIsNetworkDeployment()
        {
            if (!string.IsNullOrEmpty(_currentPath) && _currentPath.Contains("AppData\\Local\\Apps"))
            {
                return true;
            }

            return false;
        }
    }
}
