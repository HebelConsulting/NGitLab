﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NGitLab.Models;
using NUnit.Framework;
using Polly;

namespace NGitLab.Tests.Docker
{
    public sealed class GitLabTestContext : IDisposable
    {
        private static readonly Policy s_gitlabRetryPolicy = Policy.Handle<GitLabException>().WaitAndRetry(10, _ => TimeSpan.FromSeconds(1));
        private static readonly HashSet<string> s_generatedValues = new HashSet<string>(StringComparer.Ordinal);
        private static readonly SemaphoreSlim s_prepareRunnerLock = new SemaphoreSlim(1, 1);

        private readonly CustomRequestOptions _customRequestOptions = new CustomRequestOptions();
        private readonly List<IGitLabClient> _clients = new List<IGitLabClient>();

        public GitLabDockerContainer DockerContainer { get; set; }

        private GitLabTestContext(GitLabDockerContainer container)
        {
            DockerContainer = container;
            AdminClient = CreateClient(DockerContainer.Credentials.AdminUserToken);
            Client = CreateClient(DockerContainer.Credentials.UserToken);

            HttpClient = new HttpClient()
            {
                BaseAddress = DockerContainer.GitLabUrl,
            };

            AdminHttpClient = new HttpClient()
            {
                BaseAddress = DockerContainer.GitLabUrl,
                DefaultRequestHeaders =
                {
                    { "Cookie", "_gitlab_session=" + DockerContainer.Credentials.AdminCookies },
                },
            };
        }

        public static async Task<GitLabTestContext> CreateAsync()
        {
            var container = await GitLabDockerContainer.GetOrCreateInstance().ConfigureAwait(false);
            return new GitLabTestContext(container);
        }

        public HttpClient HttpClient { get; }

        public HttpClient AdminHttpClient { get; }

        public IGitLabClient AdminClient { get; }

        public IGitLabClient Client { get; }

        public WebRequest LastRequest => _customRequestOptions.AllRequests[_customRequestOptions.AllRequests.Count - 1];

        private static bool IsUnique(string str)
        {
            lock (s_generatedValues)
            {
                return s_generatedValues.Add(str);
            }
        }

        public IGitLabClient CreateNewUserAsync() => CreateNewUserAsync(out _);

        public IGitLabClient CreateNewUserAsync(out User user)
        {
            var username = "user_" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
            var email = username + "@dummy.com";
            var password = "Pa$$w0rd";
            var client = AdminClient;

            user = client.Users.Create(new Models.UserUpsert()
            {
                Email = email,
                Username = username,
                Name = username,
                Password = password,
                CanCreateGroup = true,
                SkipConfirmation = true,
            });

            var token = client.Users.CreateToken(new Models.UserTokenCreate()
            {
                UserId = user.Id,
                Name = "UnitTest",
                Scopes = new[] { "api", "read_user" },
            });
            return CreateClient(token.Token);
        }

        public Project CreateProject(Action<ProjectCreate> configure = null, bool initializeWithCommits = false)
        {
            var client = Client;
            var projectCreate = new ProjectCreate()
            {
                Name = GetUniqueRandomString(),
                Description = "Test project",
                IssuesEnabled = true,
                MergeRequestsEnabled = true,
                SnippetsEnabled = true,
                VisibilityLevel = VisibilityLevel.Internal,
                WikiEnabled = true,
            };

            configure?.Invoke(projectCreate);
            var project = client.Projects.Create(projectCreate);

            if (initializeWithCommits)
            {
                AddSomeCommits();
            }

            return project;

            void AddSomeCommits()
            {
                s_gitlabRetryPolicy.Execute(() =>
                    client.GetRepository(project.Id).Files.Create(new FileUpsert
                    {
                        Branch = "master",
                        CommitMessage = "add readme",
                        Path = "README.md",
                        RawContent = "this project should only live during the unit tests, you can delete if you find some",
                    }));

                for (var i = 0; i < 3; i++)
                {
                    s_gitlabRetryPolicy.Execute(() =>
                        client.GetRepository(project.Id).Files.Create(new FileUpsert
                        {
                            Branch = "master",
                            CommitMessage = $"add test file {i}",
                            Path = $"TestFile{i}.txt",
                            RawContent = "this project should only live during the unit tests, you can delete if you find some",
                        }));
                }
            }
        }

        public Group CreateGroup(Action<GroupCreate> configure = null)
        {
            var client = Client;
            var name = GetUniqueRandomString();
            var groupCreate = new GroupCreate()
            {
                Name = name,
                Path = name,
                Description = "Test group",
                Visibility = VisibilityLevel.Internal,
            };

            configure?.Invoke(groupCreate);
            return client.Groups.Create(groupCreate);
        }

        public (Project Project, MergeRequest MergeRequest) CreateMergeRequest()
        {
            var client = Client;
            var project = CreateProject();
            s_gitlabRetryPolicy.Execute(() => client.GetRepository(project.Id).Files.Create(new FileUpsert { Branch = "master", CommitMessage = "test", Content = "test", Path = "test.md" }));
            s_gitlabRetryPolicy.Execute(() => client.GetRepository(project.Id).Branches.Create(new BranchCreate { Name = "branch", Ref = "master" }));
            s_gitlabRetryPolicy.Execute(() => client.GetRepository(project.Id).Files.Update(new FileUpsert { Branch = "branch", CommitMessage = "test", Content = "test2", Path = "test.md" }));
            var mr = client.GetMergeRequest(project.Id).Create(new MergeRequestCreate
            {
                SourceBranch = "branch",
                TargetBranch = "master",
                Title = "test",
            });

            return (project, mr);
        }

        [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "By design")]
        public string GetUniqueRandomString()
        {
            for (var i = 0; i < 1000; i++)
            {
                var result = "GitLabClientTests_" + Guid.NewGuid().ToString("N");
                if (IsUnique(result))
                    return result;
            }

            throw new InvalidOperationException("Cannot generate a new random unique string");
        }

        [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "By design")]
        public int GetRandomNumber()
        {
            return RandomNumberGenerator.GetInt32(int.MaxValue);
        }

        private IGitLabClient CreateClient(string token)
        {
            var client = new GitLabClient(DockerContainer.GitLabUrl.ToString(), token, _customRequestOptions);
            _clients.Add(client);
            return client;
        }

        internal static bool IsContinuousIntegration()
        {
            return string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Environment.GetEnvironmentVariable("GITLAB_CI"), "true", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<IDisposable> StartRunnerForOneJobAsync(int projectId)
        {
            // Download runner (windows / linux, GitLab version)
            await s_prepareRunnerLock.WaitAsync().ConfigureAwait(false);

            try
            {
                var path = Path.Combine(Path.GetTempPath(), "GitLabClient", "Runners", "gitlab-runner.exe");
                if (!File.Exists(path))
                {
                    if (!File.Exists(path))
                    {
                        Uri url;
                        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        {
                            url = new Uri($"https://gitlab-runner-downloads.s3.amazonaws.com/latest/binaries/gitlab-runner-windows-amd64.exe");
                        }
                        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                        {
                            url = new Uri($"https://gitlab-runner-downloads.s3.amazonaws.com/latest/binaries/gitlab-runner-linux-amd64");
                        }
                        else
                        {
                            throw new InvalidOperationException($"OS '{RuntimeInformation.OSDescription}' is not supported");
                        }

                        await using var stream = await HttpClient.GetStreamAsync(url).ConfigureAwait(false);
                        Directory.CreateDirectory(Path.GetDirectoryName(path));

                        await using var fs = File.OpenWrite(path);
                        await stream.CopyToAsync(fs).ConfigureAwait(false);
                    }
                }

                TestContext.WriteLine("Test runner downloaded");
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    using var chmodProcess = Process.Start("chmod", "+x \"" + path + "\"");
                    chmodProcess.WaitForExit();
                    if (chmodProcess.ExitCode != 0)
                        throw new InvalidOperationException("chmod failed");

                    TestContext.WriteLine("chmod run");
                }

                if (!IsContinuousIntegration())
                {
                    // Update the git configuration to remove any proxy for this host
                    // git config --global http.http://localhost:48624.proxy ""
                    using var gitConfigProcess = Process.Start("git", "config --global http.http://localhost:48624.proxy \"\"");
                    gitConfigProcess.WaitForExit();
                    if (gitConfigProcess.ExitCode != 0)
                        throw new InvalidOperationException("git config failed");

                    TestContext.WriteLine("git config changed");
                }

                var project = AdminClient.Projects[projectId];
                if (project.RunnersToken == null)
                    throw new InvalidOperationException("Project runner token is null");

                var runner = AdminClient.Runners.Register(new RunnerRegister { Token = project.RunnersToken, Description = "test" });
                if (runner.Token == null)
                    throw new InvalidOperationException("Runner token is null");

                TestContext.WriteLine($"Runner registered '{runner.Token}'");

                // Use run-single, so we don't need to manage the configuration file.
                // Also, I don't think there is a need to run multiple jobs in a test
                var buildDir = Path.Combine(Path.GetTempPath(), "GitLabClient", "Runners", "build");
                Directory.CreateDirectory(buildDir);
                var psi = new ProcessStartInfo
                {
                    FileName = path,
                    ArgumentList =
                {
                    "run-single",
                    "--url", DockerContainer.GitLabUrl.ToString(),
                    "--executor", "shell",
                    "--shell", RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "powershell" : "pwsh",
                    "--builds-dir", buildDir,
                    "--wait-timeout", "240", // in seconds
                    "--token", runner.Token,
                },
                    CreateNoWindow = true,
                    ErrorDialog = false,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                };
                var process = Process.Start(psi);
                if (process == null)
                    throw new InvalidOperationException("Cannot start the runner");

                if (process.HasExited)
                    throw new InvalidOperationException("The runner has exited");

                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                process.ErrorDataReceived += (sender, e) => Console.Error.WriteLine(e.Data);
                process.OutputDataReceived += (sender, e) => Console.Error.WriteLine(e.Data);

                TestContext.WriteLine($"Runner started for project '{project.Id}' on '{DockerContainer.GitLabUrl}'");
                return new ProcessKill(process);
            }
            finally
            {
                s_prepareRunnerLock.Release();
            }
        }

        public static async Task<T> RetryUntilAsync<T>(Func<Task<T>> action, Func<T, bool> predicate, TimeSpan timeSpan)
        {
            using var cts = new CancellationTokenSource(timeSpan);
            return await RetryUntilAsync(action, predicate, cts.Token).ConfigureAwait(false);
        }

        public static async Task<T> RetryUntilAsync<T>(Func<Task<T>> action, Func<T, bool> predicate, CancellationToken cancellationToken)
        {
            var result = await action().ConfigureAwait(false);
            while (!predicate(result))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

                result = await action().ConfigureAwait(false);
            }

            return result;
        }

        public static async Task<T> RetryUntilAsync<T>(Func<T> action, Func<T, bool> predicate, TimeSpan timeSpan)
        {
            using var cts = new CancellationTokenSource(timeSpan);
            return await RetryUntilAsync(action, predicate, cts.Token).ConfigureAwait(false);
        }

        public static async Task<T> RetryUntilAsync<T>(Func<T> action, Func<T, bool> predicate, CancellationToken cancellationToken)
        {
            var result = action();
            while (!predicate(result))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);

                result = action();
            }

            return result;
        }

        public void Dispose()
        {
        }

        private sealed class ProcessKill : IDisposable
        {
            private readonly Process _process;

            public ProcessKill(Process process)
            {
                _process = process;
            }

            public void Dispose()
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit();
            }
        }

        /// <summary>
        /// Stores all the web requests in a list.
        /// </summary>
        private sealed class CustomRequestOptions : RequestOptions
        {
            private readonly List<WebRequest> _allRequests = new List<WebRequest>();

            public IReadOnlyList<WebRequest> AllRequests => _allRequests;

            public CustomRequestOptions()
                : base(retryCount: 10, retryInterval: TimeSpan.FromSeconds(1), isIncremental: true)
            {
            }

            public override WebResponse GetResponse(HttpWebRequest request)
            {
                lock (_allRequests)
                {
                    _allRequests.Add(request);
                }

                return base.GetResponse(request);
            }
        }
    }
}