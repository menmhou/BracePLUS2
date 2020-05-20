﻿ using System;
using System.IO;
using System.Diagnostics;
using System.Collections.ObjectModel;

using Xamarin.Forms;
using static BracePLUS.Extensions.Constants;

using BracePLUS.Models;
using BracePLUS.Extensions;
using MvvmCross.ViewModels;
using System.Collections.Generic;
using BracePLUS.Events;
using System.Threading.Tasks;
using BracePLUS.Views;
using Microsoft.AppCenter.Crashes;
using Xamarin.Essentials;
using BracePLUS.Services;
using Microsoft.WindowsAzure.Storage.Blob;
using Plugin.Toast;

namespace BracePLUS.ViewModels
{
    public class HistoryViewModel : MvxViewModel
    {
        // Public Properties
        #region Refreshing
        private bool _isRefreshing;
        public bool IsRefreshing 
        {
            get => _isRefreshing; 
            set
            {
                _isRefreshing = value;
                RaisePropertyChanged(() => IsRefreshing);
            }
        }
        public Command RefreshCommand { get; set; }
        #endregion
        #region DataObjectsList
        private ObservableCollection<DataObject> _dataObjects;
        public ObservableCollection<DataObject> DataObjects 
        {
            get => _dataObjects;
            set
            {
                _dataObjects = value;
                RaisePropertyChanged(() => DataObjects);
            }
        }
        #endregion
        #region View Properties
        private double _listViewHeight;
        public double ListViewHeight
        {
            get => _listViewHeight;
            set
            {
                _listViewHeight = value;
                RaisePropertyChanged(() => ListViewHeight);
            }
        }

        private DataObject _selectedObject;
        public DataObject SelectedObject
        {
            get => _selectedObject;
            set
            {
                _selectedObject = value;
                RaisePropertyChanged(() => SelectedObject);

                if (_selectedObject != null)
                {
                    HandleSelection(_selectedObject);
                    _selectedObject = null;
                }
            }
        }
        #endregion

        public INavigation Navigation { get; set; }

        // Public Commands
        public Command CloudSyncCommand { get; set; }
        public Command PhoneSyncCommand { get; set; }

        // Private Properties
        private readonly MessageHandler handler;

        public HistoryViewModel()
        {
            handler = new MessageHandler();
            DataObjects = new ObservableCollection<DataObject>();

            ListViewHeight = DeviceDisplay.MainDisplayInfo.Height;
            SelectedObject = null;

            // Commands
            RefreshCommand = new Command(() => ExecuteRefreshCommand());
            CloudSyncCommand = new Command(async () => await ExecuteCloudSyncCommand());
            PhoneSyncCommand = new Command(async () => await ExecutePhoneSyncCommand());

            // Events
            App.Client.FileSyncComplete += (s, e) =>
            {
                AddMobileFilenames(e.Files);
                RefreshObjects();
            };
            App.Client.SystemEvent += (s, e) =>
            {
                switch (e.Status)
                {
                    case DOWNLOAD_FINISH:
                        UpdateObject(e.Filename);
                        RefreshObjects();
                        break;

                    case FILE_WRITTEN:
                        RefreshObjects();
                        break;
                }
            };

            MessagingCenter.Subscribe<InspectViewModel, DataObject>(this, "Remove", (sender, arg) =>
            {
                DataObjects.Remove(arg);
                LoadLocalFiles();
            });

            RefreshObjects();
        }

        #region Command Methods
        private void ExecuteRefreshCommand()
        {
            IsRefreshing = true;
            RefreshObjects();
            IsRefreshing = false;
        }

        private async Task ExecutePhoneSyncCommand()
        {
            if (App.IsConnected)
            {
                await App.Client.GetMobileFiles();
            }
            else
            {
                await Application.Current.MainPage.DisplayAlert
                    ("Not Connected", "Please connect to Brace+ to sync files.", "OK");
            }
        }
        private async Task ExecuteCloudSyncCommand()
        {
            CrossToastPopUp.Current.ShowToastMessage("Syncing files with cloud...");
            // First get all filenames from cloud
            var blobs = await BlobStorageService.GetBlobs<CloudBlockBlob>("patient0");

            // Now go through all local files and any not pushed in cloud; push.
            foreach (var obj in DataObjects)
            {
                if (obj.IsDownloaded)
                {                       
                    var bytes = await BlobStorageService.SaveBlockBlob("patient0", obj.RawData, obj.Filename);

                    // Rewrite file data
                    FileManager.RewriteFile(bytes, obj.Filename);
                }   
            }

            // Now go through all cloud files and any not pulled to local; pull.
            foreach (var blob in blobs)
            {
                // Check if file already exists...
                bool skip = false;

                foreach (var obj in DataObjects)
                    if (obj.Filename == blob.Name) skip = true;
                

                if (!skip) await BlobStorageService.DownloadBlobData(blob);
            }

            RefreshObjects();

            CrossToastPopUp.Current.ShowToastMessage("Cloud sync finished.");
        }
        #endregion

        public void RefreshObjects()
        {
            var msg = "HISTORY: Refreshing files...";
            Debug.WriteLine(msg);
            MessagingCenter.Send(App.Client, "StatusMessage", msg);

            LoadLocalFiles();

            int downloaded = 0;
            double avgs_sum = 0.0;
            foreach (DataObject obj in DataObjects)
            {
                obj.DownloadLocalData(obj.Directory);
                obj.Analyze();

                if (obj.IsDownloaded)
                {
                    avgs_sum += obj.AveragePressure;
                    downloaded++;
                    if (obj.MaxPressure > App.GlobalMax) App.GlobalMax = obj.MaxPressure;
                }
            }

            App.GlobalAverage = avgs_sum / downloaded;
        }

        private async void HandleSelection(DataObject item)
        {
            // Check for null and proceed.
            if (item == null)
                return;

            // Inspect file...
            try
            {
                await Navigation.PushAsync(new Inspect(item));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Async nav push to new file inspect page failed: {ex.Message}");
            }

            SelectedObject = null;
        }

        private void LoadLocalFiles()
        {
            ObservableCollection<DataObject> tempData = new ObservableCollection<DataObject>();

            var files = Directory.EnumerateFiles(App.FolderPath, "*.txt");

            foreach (var filename in files)
            {
                // Get info about file
                FileInfo fi = new FileInfo(filename);
               
                // Download data ready to be read by data object
                var data = File.ReadAllBytes(filename);
                var header = new byte[3];
                Array.Copy(data, header, 3);

                DataObject dataObject = new DataObject
                {
                    Size = fi.Length,
                    Date = handler.DecodeFilename(fi.Name, file_format: FILE_FORMAT_MMDDHHmm),
                    Filename = fi.Name,
                    Directory = filename,
                    Location = handler.DecodeLocation(header)[0],
                    Tag = handler.DecodeLocation(header)[1],
                    TagColour = ((header[0] & 0x10) == 0x10) ? CLOUD_INDICATOR : Color.Gray,
                    RawData = data,
                    IsDownloaded = (data.Length > 6) ? true : false
                };

                tempData.Add(dataObject);
            }
            DataObjects = tempData;

            ReorderDataObjects();
        }

        private void ReorderDataObjects()
        {
            var data = DataObjects;

            try
            {
                for (int j = 0; j < data.Count; j++)
                {
                    for (int i = 0; i < data.Count - 1; i++)
                    {
                        try
                        {
                            // Get date of current and next object
                            int date1 = int.Parse(data[i].Filename.Remove(8));
                            int date2 = int.Parse(data[i + 1].Filename.Remove(8));

                            // If date2 > date1, respective dataobjects swap
                            if (date2 > date1)
                            {
                                // Create temp data objects
                                DataObject temp_i = data[i];
                                DataObject temp_i1 = data[i + 1];

                                // Remove from collection
                                data.Remove(temp_i);
                                data.Remove(temp_i1);

                                // Put back in opposite places
                                data.Insert(i, temp_i1);
                                data.Insert(i + 1, temp_i);
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("HISTORY: Object reordering failed: " + ex.Message);
                        }
                    }
                }
                DataObjects = data;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("HISTORY: Object reordering failed: " + ex.Message);
            }
        }

        // Load filenames pulled from mobile into locally stored empty files.
        private void AddMobileFilenames(List<string> files)
        {
            foreach (var _file in files)
            {
                // Create file instance
                try
                {
                    // Add usable extension
                    var filename = Path.Combine(App.FolderPath, _file.Remove(8) + ".txt");
                    FileStream file = new FileStream(filename, FileMode.CreateNew, FileAccess.Write);

                    // File header
                    byte[] header = new byte[3] { 0x0D, 0x0E, 0x0F };
                    file.Write(header, 0, header.Length);
                    file.Close();

                    var msg = $"Internally written file: {_file}";
                    Debug.WriteLine(msg);
                    MessagingCenter.Send(App.Client, "StatusMessage", msg);
                }
                catch (IOException ex)
                {
                    Debug.WriteLine(ex.Message);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("File writing failed with exception: " + ex.Message);
                }
            }
        }

        private void UpdateObject(string objName)
        {
            // Scan through all objects, if found update data and analyze.
            foreach (var obj in DataObjects)
            {
                if (obj.Filename == objName)
                {
                    obj.DownloadLocalData(obj.Directory);
                    //Debug.WriteLine($"Downloaded data length: {obj.Data.Length}");
                    obj.IsDownloaded = true;
                    obj.Analyze();
                }
            }
        }
    }
}
