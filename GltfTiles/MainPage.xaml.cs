//-----------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.Preview.Holographic;
using Windows.Data.Json;
using Windows.Networking.Sockets;
using Windows.Perception.Spatial;
using Windows.Security.Credentials;
using Windows.Security.Cryptography.Certificates;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System.Profile;
using Windows.UI.Core;
using Windows.UI.StartScreen;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace GltfTiles
{
    public enum TraceLevel
    {
        Always = 0,
        Critical = 1,
        Error = 2,
        Warning = 3,
        Info = 4,
        Verbose = 5,
    }

    public struct TraceEntry
    {
        public string TimeStamp;
        public TraceLevel Level;
        public string Message;
    }

    public static class Images
    {
        public static BitmapImage Error = new BitmapImage(new Uri("ms-appx:///assets/error.png"));
        public static BitmapImage Warning = new BitmapImage(new Uri("ms-appx:///assets/warning.png"));
        public static BitmapImage Info = new BitmapImage(new Uri("ms-appx:///assets/info.png"));
    }


    public sealed class TraceLevelConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var level = value as TraceLevel?;
            if (level <= TraceLevel.Error)
            {
                return Images.Error;
            }
            else if (level == TraceLevel.Warning)
            {
                return Images.Warning;
            }
            else
            {
                return Images.Info;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }


    public sealed partial class MainPage : Page
    {
        public ObservableCollection<TraceEntry> Items = new ObservableCollection<TraceEntry>();

        private FileOpenPicker picker;
        private StorageFile glbPath;
        private MessageWebSocket m_webSocket;

        private MessageWebSocket webSocket {
            get { return m_webSocket; }
            set
            {
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ShowLogViewer = value != null;
                    ConnectionMissing = value == null;
                });
                m_webSocket = value;
            }
        }

        private static int GetDefaultPort()
        {
            switch (AnalyticsInfo.VersionInfo.DeviceFamily)
            {
                case "Windows.Holographic":
                    return 443;
                default:
                    return 50443;
            }
        }

        public MainPage()
        {
            this.InitializeComponent();

            picker = new FileOpenPicker();
            picker.ViewMode = PickerViewMode.Thumbnail;
            picker.SuggestedStartLocation = PickerLocationId.Objects3D;
            picker.FileTypeFilter.Add(".glb");

            LoadCredential();
            ConnectToWebSocket();
        }

        string PasswordResource
        {
            get
            {
                return $"https://localhost:{WebBPort}/";
            }
        }

        private async void ConnectToWebSocket()
        {
            webSocket = new MessageWebSocket();
            webSocket.Control.IgnorableServerCertificateErrors.Add(ChainValidationResult.Untrusted);
            webSocket.Control.IgnorableServerCertificateErrors.Add(ChainValidationResult.InvalidName);
            webSocket.Control.MessageType = SocketMessageType.Utf8;
            if (!String.IsNullOrEmpty(PasswordResource) && !String.IsNullOrEmpty(WebBUsername) && !String.IsNullOrEmpty(WebBPassword))
            {
                webSocket.Control.ServerCredential = new PasswordCredential(PasswordResource, WebBUsername, WebBPassword);
            }
            webSocket.MessageReceived += WebSocket_MessageReceived;
            webSocket.Closed += WebSocket_Closed;

            var uri = new Uri($"wss://localhost:{WebBPort}/api/etw/session/realtime");
            try
            {
                await webSocket.ConnectAsync(uri);
                var enableMsg = "provider 9f7e92de-9bd1-5b43-9cbd-e332a6ed01e6 enable 5";
                await SendWebSocketMessageAsync(enableMsg);
                StoreCredential();
            }
            catch (Exception e)
            {
                webSocket = null;
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ConnectionErrorMsg = e.Message;
                });
            }
        }

        private void StoreCredential()
        {
            var vault = new PasswordVault();
            vault.Add(new PasswordCredential(PasswordResource, WebBUsername, WebBPassword));
        }

        private void LoadCredential()
        {
            var vault = new PasswordVault();
            try
            {
                var cred = vault.FindAllByResource(PasswordResource)?.FirstOrDefault();
                if (cred != null)
                {
                    cred.RetrievePassword();
                    WebBUsername = cred.UserName;
                    WebBPassword = cred.Password;
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine("LoadCredential: " + e.ToString());
            }
        }

        private void WebSocket_Closed(IWebSocket sender, WebSocketClosedEventArgs args)
        {
            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ConnectionErrorMsg = "Code: " + args.Code + ", Reason: \"" + args.Reason + "\"";
            });
            webSocket = null;
        }

        private void WebSocket_MessageReceived(MessageWebSocket sender, MessageWebSocketMessageReceivedEventArgs args)
        {
            try
            {
                using (DataReader dataReader = args.GetDataReader())
                {
                    dataReader.UnicodeEncoding = UnicodeEncoding.Utf8;
                    string message = dataReader.ReadString(dataReader.UnconsumedBufferLength);
                    JsonValue value;
                    if (JsonValue.TryParse(message, out value))
                    {
                        UpdateMessageList(value);
                    }
                }
            }
            catch (Exception e)
            {
                var task = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    ConnectionErrorMsg = e.Message;
                });
                webSocket = null;
            }
        }

        private void UpdateMessageList(JsonValue value)
        {
            if (value == null || value.ValueType != JsonValueType.Object)
            {
                return;
            }
            var valueObj = value.GetObject();
            var eventsArr = valueObj.GetNamedArray("Events");
            var newEvents = new List<TraceEntry>();
            foreach (var evtValue in eventsArr)
            {
                if (evtValue.ValueType != JsonValueType.Object)
                {
                    continue;
                }
                var evtObj = evtValue.GetObject();
                if (!evtObj.ContainsKey("TaskName") || evtObj.GetNamedString("TaskName") != "Log")
                {
                    continue;
                }
                var fileTime = evtObj.GetNamedNumber("Timestamp");
                var timeStamp = DateTime.FromFileTime((long)fileTime);
                newEvents.Add(new TraceEntry()
                {
                    TimeStamp = timeStamp.ToString("u"),
                    Level = (TraceLevel)evtObj.GetNamedNumber("Level"),
                    Message = evtObj.GetNamedString("msg").Trim('"'),
                });
            }

            var task = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                foreach (var item in newEvents)
                {
                    Items.Add(item);
                }

                LogScroller.UpdateLayout();
                LogScroller.ChangeView(0, LogScroller.ScrollableHeight, null);
            });
        }


        private async Task SendWebSocketMessageAsync(string message)
        {
            if (webSocket != null)
            {
                using (var dataWriter = new DataWriter(webSocket.OutputStream))
                {
                    dataWriter.WriteString(message);
                    await dataWriter.StoreAsync();
                    dataWriter.DetachStream();
                }
            }
        }

        public string ConnectionErrorMsg
        {
            get { return (string)GetValue(ConnectionErrorMsgProperty); }
            private set { SetValue(ConnectionErrorMsgProperty, value); }
        }

        public static readonly DependencyProperty ConnectionErrorMsgProperty =
          DependencyProperty.Register("ConnectionErrorMsg", typeof(string), typeof(MainPage), new PropertyMetadata(""));

        public string GlbPath
        {
            get { return (string)GetValue(GlbPathProperty); }
            private set { SetValue(GlbPathProperty, value); }
        }

        public static readonly DependencyProperty GlbPathProperty =
          DependencyProperty.Register("GlbPath", typeof(string), typeof(MainPage), new PropertyMetadata("Not yet picked"));

        public bool CreateWithBoundingBox
        {
            get { return (bool)GetValue(CreateWithBoundingBoxProperty); }
            set { SetValue(CreateWithBoundingBoxProperty, value); }
        }
        public static readonly DependencyProperty CreateWithBoundingBoxProperty =
            DependencyProperty.Register("CreateWithBoundingBox", typeof(bool), typeof(MainPage), new PropertyMetadata(true));

        public bool DoNotActivate
        {
            get { return (bool)GetValue(DoNotActivateProperty); }
            set { SetValue(DoNotActivateProperty, value); }
        }
        public static readonly DependencyProperty DoNotActivateProperty =
            DependencyProperty.Register("DoNotActivate", typeof(bool), typeof(MainPage), new PropertyMetadata(true));

        public string BoundingBoxExtents
        {
            get { return (string)GetValue(BoundingBoxExtentsProperty); }
            set { SetValue(BoundingBoxExtentsProperty, value); }
        }

        public static readonly DependencyProperty BoundingBoxExtentsProperty =
            DependencyProperty.Register("BoundingBoxExtents", typeof(string), typeof(MainPage), new PropertyMetadata("1, 1, 1"));

        public string BoundingBoxCenter
        {
            get { return (string)GetValue(BoundingBoxCenterProperty); }
            set { SetValue(BoundingBoxCenterProperty, value); }
        }

        public static readonly DependencyProperty BoundingBoxCenterProperty =
            DependencyProperty.Register("BoundingBoxCenter", typeof(string), typeof(MainPage), new PropertyMetadata("0, 0, 0"));

        public string WebBUsername
        {
            get { return (string)GetValue(WebBUsernameProperty); }
            set { SetValue(WebBUsernameProperty, value); }
        }

        public static readonly DependencyProperty WebBUsernameProperty =
            DependencyProperty.Register("WebBUsername", typeof(string), typeof(MainPage), new PropertyMetadata(""));

        public string WebBPassword
        {
            get { return (string)GetValue(WebBPasswordProperty); }
            set { SetValue(WebBPasswordProperty, value); }
        }

        public static readonly DependencyProperty WebBPasswordProperty =
            DependencyProperty.Register("WebBPassword", typeof(string), typeof(MainPage), new PropertyMetadata(""));

        public bool ConnectionMissing
        {
            get { return (bool)GetValue(ConnectionMissingProperty); }
            set { SetValue(ConnectionMissingProperty, value); }
        }

        public static readonly DependencyProperty ConnectionMissingProperty =
            DependencyProperty.Register("ConnectionMissing", typeof(bool), typeof(MainPage), new PropertyMetadata(true));
        
        public bool ShowLogViewer
        {
            get { return (bool)GetValue(ShowLogViewerProperty); }
            set { SetValue(ShowLogViewerProperty, value); }
        }

        public static readonly DependencyProperty ShowLogViewerProperty =
            DependencyProperty.Register("ShowLogViewer", typeof(bool), typeof(MainPage), new PropertyMetadata(false));

        public int WebBPort
        {
            get { return (int)GetValue(WebBPortProperty); }
            set { SetValue(WebBPortProperty, value); }
        }

        public static readonly DependencyProperty WebBPortProperty =
            DependencyProperty.Register("WebBPort", typeof(string), typeof(MainPage), new PropertyMetadata(GetDefaultPort()));

        protected override void OnNavigatedTo(NavigationEventArgs args)
        {
            LaunchActivatedEventArgs launchArgs = args.Parameter as LaunchActivatedEventArgs;
        }

        private void OnPlaceGlb(object sender, RoutedEventArgs e)
        {
            CreateSecondaryTile();
        }

        private async void OnSelectGlb(object sender, RoutedEventArgs e)
        {
            await SelectGlb();
        }

        private async Task<bool> SelectGlb()
        {
            glbPath = await picker.PickSingleFileAsync();
            if (glbPath != null)
            {
                GlbPath = glbPath.Name;
            }

            return glbPath != null;
        }

        async Task<string> CopyAssetToLocalAppData()
        {
            const string SpecialFolder = "GlbTiles";

            StorageFolder folder;
            try
            {
                folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    SpecialFolder,
                    CreationCollisionOption.OpenIfExists);
            }
            catch
            {
                var dialog = new ContentDialog()
                {
                    Content = "Could not create subfolder",
                    CloseButtonText = "Ok",
                };
                await dialog.ShowAsync();
                return null;
            }

            var copyFile = await glbPath.CopyAsync(folder, glbPath.Name, NameCollisionOption.GenerateUniqueName);

            return Path.Combine("ms-appdata:///local/", SpecialFolder, glbPath.Name);
        }

        string GenerateTileId()
        {
            // Create a guid to use as a unique tile id
            Guid guid = Guid.NewGuid();
            string guidString = guid.ToString();

            // The tile id doesn't allow brackets, so trim them off the guid
            string guidStringTrimmed = guidString.Substring(1, guidString.Length - 2);
            return guidStringTrimmed;
        }

        float ParseWithDefault(string input, float defaultValue)
        {
            float parsed;
            parsed = float.TryParse(input, out parsed) ? parsed : defaultValue;
            return parsed;
        }

        async void CreateSecondaryTile()
        {
            // Warn the user that they need to be in the headset for anything interesting to happen.
            if (!HolographicApplicationPreview.IsCurrentViewPresentedOnHolographicDisplay())
            {
                ContentDialog dialog = new ContentDialog()
                {
                    Title = "Secondary Tile",
                    Content = "3D content can be created only while the view is being displayed in a Mixed Reality headset.",
                    CloseButtonText = "Ok",
                };
                await dialog.ShowAsync();
                return;
            }

            try
            {
                if (glbPath == null && !await SelectGlb())
                {
                    return;
                }

                // Create a guid to use as a unique tile id
                string tileId = GenerateTileId();

                var logoUri = new Uri("ms-appx:///assets/Square150x150Logo.png");

                var tile = new SecondaryTile(
                        tileId, // TileId
                        glbPath.Name, // DisplayName
                        tileId, // Arguments
                        logoUri, // Square150x150Logo
                        TileSize.Square150x150); // DesiredSize

                TileMixedRealityModel model = tile.VisualElements.MixedRealityModel;

                var tcs = new TaskCompletionSource<string>();

                var copiedPath = await CopyAssetToLocalAppData();
                if (String.IsNullOrEmpty(copiedPath))
                {
                    return;
                }

                model.Uri = new Uri(copiedPath);

                if (CreateWithBoundingBox)
                {
                    var boundingBoxCenterFloats = BoundingBoxCenter.Split(',').Select(s => ParseWithDefault(s, 0f)).ToArray();
                    var boundingBoxExtentsFloats = BoundingBoxExtents.Split(',').Select(s => ParseWithDefault(s, 1f)).ToArray();

                    var boundingBox = new SpatialBoundingBox();
                    if (boundingBoxCenterFloats.Length == 3)
                    {
                        boundingBox.Center = new Vector3(boundingBoxCenterFloats[0], boundingBoxCenterFloats[1], boundingBoxCenterFloats[2]);
                    }
                    if (boundingBoxExtentsFloats.Length == 3)
                    {
                        boundingBox.Extents = new Vector3(boundingBoxExtentsFloats[0], boundingBoxExtentsFloats[1], boundingBoxExtentsFloats[2]);
                    }

                    model.BoundingBox = boundingBox;
                }

                if (DoNotActivate)
                {
                    model.ActivationBehavior = TileMixedRealityModelActivationBehavior.None;
                }

                // Commit the tile
                bool created = await tile.RequestCreateAsync();
                if (!created)
                {
                    ContentDialog dialog = new ContentDialog()
                    {
                        Title = "Secondary Tile",
                        Content = "Creation denied",
                        CloseButtonText = "Ok",
                    };
                    await dialog.ShowAsync();
                }
            }
            catch
            {
                ContentDialog dialog = new ContentDialog()
                {
                    Title = "Secondary Tile",
                    Content = "Failed to create",
                    CloseButtonText = "Ok",
                };
                await dialog.ShowAsync();
            }
        }

        private void OnAttach(object sender, RoutedEventArgs e)
        {
            ConnectToWebSocket();
        }
    }
}
