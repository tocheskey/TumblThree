﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using TumblThree.Applications.Crawler;
using TumblThree.Applications.DataModels;
using TumblThree.Applications.Properties;
using TumblThree.Applications.Services;
using TumblThree.Domain;
using TumblThree.Domain.Models;

namespace TumblThree.Applications.Downloader
{
    [Export(typeof(IDownloader))]
    [ExportMetadata("BlogType", BlogTypes.tumblr)]
    public class TumblrBlogCrawler : AbstractCrawler, ICrawler
    {
        private string authentication = String.Empty;

        public TumblrBlogCrawler(IShellService shellService, CancellationToken ct, PauseToken pt,
            IProgress<DownloadProgress> progress, ICrawlerService crawlerService, ISharedCookieService cookieService, IDownloader downloader, BlockingCollection<TumblrPost> producerConsumerCollection, IBlog blog)
            : base(shellService, ct, pt, progress, crawlerService, cookieService, downloader, producerConsumerCollection, blog)
        {
        }

        public override async Task IsBlogOnlineAsync()
        {
            try
            {
                blog.Online = true;
                await UpdateAuthentication();
                string document = await RequestDataAsync(blog.Url);
                CheckIfPasswordProtecedBlog(document);
                CheckIfHiddenBlog(document);
            }
            catch (WebException)
            {
                blog.Online = false;
            }
        }

        private void CheckIfPasswordProtecedBlog(string document)
        {
            if (Regex.IsMatch(document, "<form id=\"auth_password\" method=\"post\">"))
            {
                Logger.Error("TumblrBlogCrawler:IsBlogOnlineAsync:PasswordProtectedBlog {0}", Resources.PasswordProtected, blog.Name);
                shellService.ShowError(new WebException(), Resources.PasswordProtected, blog.Name);
            }
        }

        private void CheckIfHiddenBlog(string document)
        {
            if (Regex.IsMatch(document, "/login_required/"))
            {
                Logger.Error("TumblrBlogCrawler:IsBlogOnlineAsync:NotLoggedIn {0}", Resources.NotLoggedIn, blog.Name);
                shellService.ShowError(new WebException(), Resources.NotLoggedIn, blog.Name);
                blog.Online = false;
            }
        }

        public async Task Crawl()
        {
            Logger.Verbose("TumblrBlogCrawler.Crawl:Start");

            Task grabber = GetUrlsAsync();
            Task<bool> download = downloader.DownloadBlogAsync();

            await grabber;

            UpdateProgressQueueInformation(Resources.ProgressUniqueDownloads);
            blog.DuplicatePhotos = DetermineDuplicates(PostTypes.Photo);
            blog.DuplicateVideos = DetermineDuplicates(PostTypes.Video);
            blog.DuplicateAudios = DetermineDuplicates(PostTypes.Audio);
            blog.TotalCount = (blog.TotalCount - blog.DuplicatePhotos - blog.DuplicateAudios - blog.DuplicateVideos);

            CleanCollectedBlogStatistics();

            await download;

            if (!ct.IsCancellationRequested)
            {
                blog.LastCompleteCrawl = DateTime.Now;
            }

            blog.Save();

            UpdateProgressQueueInformation("");
        }

        private async Task GetUrlsAsync()
        {
            var semaphoreSlim = new SemaphoreSlim(shellService.Settings.ConcurrentScans);
            var trackedTasks = new List<Task>();

            foreach (int crawlerNumber in Enumerable.Range(1, shellService.Settings.ConcurrentScans))
            {
                await semaphoreSlim.WaitAsync();

                trackedTasks.Add(new Func<Task>(async () =>
                {
                    tags = new List<string>();
                    if (!string.IsNullOrWhiteSpace(blog.Tags))
                    {
                        tags = blog.Tags.Split(',').Select(x => x.Trim()).ToList();
                    }

                    try
                    {
                        string document = await RequestDataAsync(blog.Url + "page/" + crawlerNumber);
                        await AddUrlsToDownloadList(document, crawlerNumber);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        semaphoreSlim.Release();
                    }
                })());
            }
            await Task.WhenAll(trackedTasks);

            producerConsumerCollection.CompleteAdding();

            //if (!ct.IsCancellationRequested)
            //{
                UpdateBlogStats();
            //}
        }

        private async Task UpdateAuthentication()
        {
            string document = await ThrottleAsync(Authenticate);
            authentication = ExtractAuthenticationKey(document);
            await UpdateCookieWithAuthentication();
        }

        private async Task<T> ThrottleAsync<T>(Func<Task<T>> method)
        {
            if (shellService.Settings.LimitConnections)
            {
                return await method();
            }
            return await method();
        }

        protected async Task<string> Authenticate()
        {
            var requestRegistration = new CancellationTokenRegistration();
            try
            {
                string url = "https://www.tumblr.com/blog_auth/" + blog.Name;
                var headers = new Dictionary<string, string>();
                HttpWebRequest request = CreatePostReqeust(url, url, headers);
                string requestBody = "password=" + blog.Password;
                using (Stream postStream = await request.GetRequestStreamAsync())
                {
                    byte[] postBytes = Encoding.ASCII.GetBytes(requestBody);
                    await postStream.WriteAsync(postBytes, 0, postBytes.Length);
                    await postStream.FlushAsync();
                }

                requestRegistration = ct.Register(() => request.Abort());
                return await ReadReqestToEnd(request);
            }
            finally
            {
                requestRegistration.Dispose();
            }
        }

        private static string ExtractAuthenticationKey(string document)
        {
            return Regex.Match(document, "name=\"auth\" value=\"([\\S]*)\"").Groups[1].Value;
        }

        protected async Task UpdateCookieWithAuthentication()
        {
            var requestRegistration = new CancellationTokenRegistration();
            try
            {
                string url = "https://" + blog.Name + ".tumblr.com/";
                string referer = "https://www.tumblr.com/blog_auth/" + blog.Name;
                var headers = new Dictionary<string, string>();
                headers.Add("DNT", "1");
                HttpWebRequest request = CreatePostReqeust(url, referer, headers);
                string requestBody = "auth=" + authentication;
                using (Stream postStream = await request.GetRequestStreamAsync())
                {
                    byte[] postBytes = Encoding.ASCII.GetBytes(requestBody);
                    await postStream.WriteAsync(postBytes, 0, postBytes.Length);
                    await postStream.FlushAsync();
                }

                requestRegistration = ct.Register(() => request.Abort());
                using (var response = await request.GetResponseAsync() as HttpWebResponse)
                {
                    cookieService.SetUriCookie(response.Cookies);
                }
            }
            finally
            {
                requestRegistration.Dispose();
            }
        }

        private async Task AddUrlsToDownloadList(string document, int crawlerNumber)
        {
            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }
                if (pt.IsPaused)
                {
                    pt.WaitWhilePausedWithResponseAsyc().Wait();
                }

                AddPhotoUrlToDownloadList(document);
                AddVideoUrlToDownloadList(document);

                Interlocked.Increment(ref numberOfPagesCrawled);
                UpdateProgressQueueInformation(Resources.ProgressGetUrlShort, numberOfPagesCrawled);
                document = await RequestDataAsync(blog.Url + "page/" + crawlerNumber);
                if (!document.Contains((crawlerNumber + 1).ToString()))
                {
                    return;
                }
                crawlerNumber += shellService.Settings.ConcurrentScans;
            }
        }

        private void AddPhotoUrlToDownloadList(string document)
        {
            if (blog.DownloadPhoto)
            {
                var regex = new Regex("\"(http[A-Za-z0-9_/:.]*media.tumblr.com[A-Za-z0-9_/:.]*(jpg|png|gif))\"");
                foreach (Match match in regex.Matches(document))
                {
                    string imageUrl = match.Groups[1].Value;
                    if (imageUrl.Contains("avatar") || imageUrl.Contains("previews"))
                        continue;
                    if (blog.SkipGif && imageUrl.EndsWith(".gif"))
                    {
                        continue;
                    }
                    imageUrl = ResizeTumblrImageUrl(imageUrl);
                    // TODO: postID
                    AddToDownloadList(new TumblrPost(PostTypes.Photo, imageUrl, Guid.NewGuid().ToString("N")));
                }
            }
        }

        private void AddVideoUrlToDownloadList(string document)
        {
            if (blog.DownloadVideo)
            {
                var regex = new Regex("\"(http[A-Za-z0-9_/:.]*.com/video_file/[A-Za-z0-9_/:.]*)\"");
                foreach (Match match in regex.Matches(document))
                {
                    string videoUrl = match.Groups[0].Value;
                    if (shellService.Settings.VideoSize == 1080)
                    {
                        // TODO: postID
                        AddToDownloadList(new TumblrPost(PostTypes.Video,
                            "https://vt.tumblr.com/" + videoUrl.Replace("/480", "").Split('/').Last() + ".mp4",
                            Guid.NewGuid().ToString("N")));
                    }
                    else if (shellService.Settings.VideoSize == 480)
                    {
                        // TODO: postID
                        AddToDownloadList(new TumblrPost(PostTypes.Video,
                            "https://vt.tumblr.com/" + videoUrl.Replace("/480", "").Split('/').Last() + "_480.mp4",
                            Guid.NewGuid().ToString("N")));
                    }
                }
            }
        }
    }
}
