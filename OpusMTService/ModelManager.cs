﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using static System.Environment;

namespace OpusMTService
{
    /// <summary>
    /// This class contains methods for checking and downloading latest models from
    /// the fiskmo model repository. 
    /// </summary>
    public class ModelManager : INotifyPropertyChanged
    {

        private List<MTModel> onlineModels;

        private ObservableCollection<MTModel> filteredOnlineModels = new ObservableCollection<MTModel>();

        public ObservableCollection<MTModel> FilteredOnlineModels
        { get => filteredOnlineModels; set { filteredOnlineModels= value; NotifyPropertyChanged(); } }

        private ObservableCollection<MTModel> localModels;

        public ObservableCollection<MTModel> LocalModels
        { get => localModels; set { localModels = value; NotifyPropertyChanged(); } }

        public event PropertyChangedEventHandler PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            if (PropertyChanged != null)
            {
                PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
            }
        }
        

        private DirectoryInfo opusModelDir;

        public DirectoryInfo OpusModelDir { get => opusModelDir; }

        internal void FilterOnlineModels(string sourceFilter, string targetFilter, string nameFilter)
        {
            var filteredModels = from model in this.onlineModels
                                 where
                                    model.SourceLanguageString.Contains(sourceFilter) &&
                                    model.TargetLanguageString.Contains(targetFilter) &&
                                    model.Name.Contains(nameFilter)
                                 select model;

            this.FilteredOnlineModels.Clear();
            foreach (var model in filteredModels)
            {
                this.FilteredOnlineModels.Add(model);
            }
        }

        public ModelManager()
        {

            this.GetOnlineModels();
            this.opusModelDir = new DirectoryInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                OpusMTServiceSettings.Default.LocalFiskmoDir,
                "models"));
            if (!this.OpusModelDir.Exists)
            {
                this.OpusModelDir.Create();
            }

            this.GetLocalModels();
            
        }

        private void GetOnlineModels()
        {
            using (var client = new WebClient())
            {
                client.DownloadStringCompleted += modelListDownloadComplete;
                client.DownloadStringAsync(new Uri(OpusMTServiceSettings.Default.ModelStorageUrl));
            }
        }

        private void modelListDownloadComplete(object sender, DownloadStringCompletedEventArgs e)
        {
            XDocument buckets = XDocument.Parse(e.Result);
            var ns = buckets.Root.Name.Namespace;
            var files = buckets.Descendants(ns + "Key").Select(x => x.Value);
            var models = files.Where(x => x.StartsWith("models") && x.EndsWith(".zip"));
            this.onlineModels = new List<MTModel>(models.Select(x => new MTModel(x.Replace("models/", "").Replace(".zip", ""))));

            foreach (var onlineModel in this.onlineModels)
            {
                var localModelPaths = this.LocalModels.Select(x => x.Path.Replace("\\", "/"));
                if (localModelPaths.Contains(onlineModel.Path))
                {
                    onlineModel.InstallStatus = "Installed";
                    onlineModel.InstallProgress = 100;
                }
            }

            this.FilterOnlineModels("", "", "");
        }

        internal void GetLocalModels()
        {
            var modelPaths = this.OpusModelDir.GetFiles("*.npz", SearchOption.AllDirectories).Select(x => x.DirectoryName).Distinct().ToList();
            this.LocalModels = new ObservableCollection<MTModel>(
                modelPaths.Select(x => new MTModel(Regex.Match(x, @"[^\\]+\\[^\\]+$").Value,x)));
        }

        internal string[] GetAllModelDirs(string sourceLang, string targetLang)
        {
            if (Directory.Exists(
                Path.Combine(this.opusModelDir.FullName, "models", $"{sourceLang}-{targetLang}")))
            {
                var languagePairModels = Directory.GetDirectories(Path.Combine(
                    this.opusModelDir.FullName, "models", $"{sourceLang}-{targetLang}"));
                return languagePairModels;
            }
            else
            {
                return null;
            }
        }

        internal string Translate(string input, string srcLangCode, string trgLangCode)
        {
            //Use the first suitable model
            var installedModel = this.LocalModels.Where(x => x.SourceLanguages.Contains(srcLangCode) && x.TargetLanguages.Contains(trgLangCode)).First();
            return installedModel.Translate(input);
        }

        internal IEnumerable<string> GetAllLanguagePairs()
        {
            //The format of the model strings is "models/<source>-target/<name>"
            return this.LocalModels.Select(x => x.Name.Split('/')[1]);
        }

        internal string GetLatestModelDir(string sourceLang, string targetLang)
        {
            var allModels = this.GetAllModelDirs(sourceLang, targetLang);
            if (allModels == null)
            {
                return null;
            }
            else
            {
                var newestLanguagePairModel = allModels.OrderBy(x => Regex.Match(x, @"\d{8}").Value).LastOrDefault();
                return newestLanguagePairModel;
            }
        }

        internal void DownloadModel(
            string newerModel,
            DownloadProgressChangedEventHandler wc_DownloadProgressChanged,
            AsyncCompletedEventHandler wc_DownloadComplete)
        {
            var downloadPath = Path.Combine(this.OpusModelDir.FullName, newerModel);

            using (var client = new WebClient())
            {
                client.DownloadProgressChanged += wc_DownloadProgressChanged;
                client.DownloadFileCompleted += wc_DownloadComplete;
                var modelUrl = $"{OpusMTServiceSettings.Default.ModelStorageUrl}/models/{newerModel}.zip";
                Directory.CreateDirectory(Path.GetDirectoryName(downloadPath));
                client.DownloadFileAsync(new Uri(modelUrl), downloadPath);
            }
        }

        internal void ExtractModel(string modelPath,bool deleteZip=false)
        {
            string modelFolder = Path.Combine(this.OpusModelDir.FullName, modelPath);
            string zipPath = modelFolder+".zip";

            this.ExtractModel(new FileInfo(zipPath), deleteZip);
        }

        //For extracting zip files
        internal void ExtractModel(FileInfo zipPath, bool deleteZip = false)
        {
            Match modelPathMatch = Regex.Match(zipPath.FullName, @".*\\([^\\]+\\[^\\]+)\.zip$");
            string modelPath = modelPathMatch.Groups[1].Value;
            string modelFolder = Path.Combine(this.OpusModelDir.FullName, modelPath);
            ZipFile.ExtractToDirectory(zipPath.FullName, modelFolder);
            if (deleteZip)
            {
                zipPath.Delete();
            }
        }


        public Boolean IsLanguagePairSupported(string sourceLang, string targetLang)
        {
            //This method is called many times (possibly for each segment), so use the cached model list instead of
            //new model fetch call to check whether pair is supported
            return this.LocalModels.Any(x => x.Name.Contains($"{sourceLang}-{targetLang}"));
        }

        //Check for existence of current Fiskmo models in ProgramData
        public string CheckForNewerModel(string sourceLang, string targetLang)
        {
            string newerModel = null;
            //var timestamps = this.GetLatestModelInfo().Select(x => Regex.Match(x, @"\d{8}").Value);
            //order models by timestamp
            var onlineModels = this.onlineModels.OrderBy(x => Regex.Match(x.Name, @"\d{8}").Value);

            if (onlineModels != null)
            {
                var newestLangpairModel = onlineModels.LastOrDefault(x => x.Name.Contains($"{sourceLang}-{targetLang}"));

                if (newestLangpairModel != null)
                {
                    var newestModelDir = Path.Combine(
                    OpusModelDir.FullName, Regex.Replace(newestLangpairModel.Name, @"\.zip$", ""));
                    if (!Directory.Exists(newestModelDir))
                    {
                        newerModel = newestLangpairModel.Name;
                    }
                }
            }

            return newerModel;
        }
        
    }
}