﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Diagnostics;
using Newtonsoft.Json;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Microsoft.ProjectOxford.Emotion;
using Microsoft.ProjectOxford.Emotion.Contract;
using Microsoft.ProjectOxford.Face;
using Microsoft.ProjectOxford.Face.Contract;
using Microsoft.ProjectOxford.Vision;
using VideoFrameAnalyzer;

namespace ReleaseYourFingers
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : System.Windows.Window
    {
        private EmotionServiceClient _emotionClient = null;
        private FaceServiceClient _faceClient = null;
        private VisionServiceClient _visionClient = null;
        private readonly FrameGrabber<LiveCameraResult> _grabber = null;
        private static readonly ImageEncodingParam[] s_jpegParams = {
            new ImageEncodingParam(ImwriteFlags.JpegQuality, 60)
        };
        private readonly CascadeClassifier _localFaceDetector = new CascadeClassifier();
        private bool _fuseClientRemoteResults;
        private LiveCameraResult _latestResultsToDisplay = null;
        private AppMode _mode;
        private DateTime _startTime;

        private VideoFrame _preFrame = null;
        private LiveCameraResult _preResults = null;
        private bool checkMove = true;

        public enum AppMode
        {
            Faces,
            Emotions,
            EmotionsWithClientFaceDetect,
            Tags,
            Celebrities
        }

        public MainWindow()
        {
            InitializeComponent();

            // Create grabber. 
            _grabber = new FrameGrabber<LiveCameraResult>();

            // Set up a listener for when the client receives a new frame.
            _grabber.NewFrameProvided += (s, e) =>
            {
                if (_mode == AppMode.EmotionsWithClientFaceDetect)
                {
                    // Local face detection. 
                    var rects = _localFaceDetector.DetectMultiScale(e.Frame.Image);
                    // Attach faces to frame. 
                    e.Frame.UserData = rects;
                }

                // The callback may occur on a different thread, so we must use the
                // MainWindow.Dispatcher when manipulating the UI. 
                this.Dispatcher.BeginInvoke((Action)(() =>
                {
                    // Display the image in the left pane.
                    LeftImage.Source = e.Frame.Image.ToBitmapSource();

                    // If we're fusing client-side face detection with remote analysis, show the
                    // new frame now with the most recent analysis available. 
                    if (_fuseClientRemoteResults)
                    {
                        RightImage.Source = VisualizeResult(e.Frame);
                    }
                }));

                // See if auto-stop should be triggered. 
                if (Properties.Settings.Default.AutoStopEnabled && (DateTime.Now - _startTime) > Properties.Settings.Default.AutoStopTime)
                {
                    _grabber.StopProcessingAsync();
                }
            };

            // Set up a listener for when the client receives a new result from an API call. 
            _grabber.NewResultAvailable += (s, e) =>
            {
                this.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (e.TimedOut)
                    {
                        MessageArea.Text = "API call timed out.";
                    }
                    else if (e.Exception != null)
                    {
                        string apiName = "";
                        string message = e.Exception.Message;
                        var faceEx = e.Exception as FaceAPIException;
                        var emotionEx = e.Exception as Microsoft.ProjectOxford.Common.ClientException;
                        var visionEx = e.Exception as Microsoft.ProjectOxford.Vision.ClientException;
                        if (faceEx != null)
                        {
                            apiName = "Face";
                            message = faceEx.ErrorMessage;
                        }
                        else if (emotionEx != null)
                        {
                            apiName = "Emotion";
                            message = emotionEx.Error.Message;
                        }
                        else if (visionEx != null)
                        {
                            apiName = "Computer Vision";
                            message = visionEx.Error.Message;
                        }
                        MessageArea.Text = string.Format("{0} API call failed on frame {1}. Exception: {2}", apiName, e.Frame.Metadata.Index, message);
                        Indicator.Fill = Brushes.Red;
                    }
                    else
                    {
                        _latestResultsToDisplay = e.Analysis;

                        // Display the image and visualization in the right pane. 
                        if (!_fuseClientRemoteResults)
                        {
                            if (checkMove)
                            {
                                bool hasPeople = HasPeople();
                                if (!hasPeople)
                                {
                                    MessageArea.Text = "No Guys！！！";
                                    Indicator.Fill = Brushes.Red;
                                    return;
                                }
                                bool still = IsStill(e.Frame);
                                if (!still)
                                {
                                    MessageArea.Text = "Please don't move！！！";
                                    Indicator.Fill = Brushes.Red;
                                    return;
                                }
                            }
                            bool faceCamera = IsAllFaceCamera();
                            if (!faceCamera)
                            {
                                MessageArea.Text = "Please see the camera！！！";
                                Indicator.Fill = Brushes.Red;
                                checkMove = false;
                                return;
                            }
                            else
                            {
                                checkMove = true;
                            }
                            
                            bool noEyeClosed = NoEyeClosed();
                            if (!noEyeClosed)
                            {
                                MessageArea.Text = "Please open your eyes！！！";
                                Indicator.Fill = Brushes.Red;
                                return;
                            }
                            
                            bool happy = IsAllHappiness();
                            if (!happy)
                            {
                                MessageArea.Text = "Please smile, Guys！！！";
                                return;
                            }
                            //MessageArea.Text = move.ToString();
                            RightImage.Source = VisualizeResult(e.Frame);
                            Indicator.Fill = Brushes.LightGreen;
                            MessageArea.Text = "";
                        }
                    }
                }));
            };

            // Create local face detector. 
            _localFaceDetector.Load("Data/haarcascade_frontalface_alt2.xml");
        }

        /// <summary> Function which submits a frame to the Face API. </summary>
        /// <param name="frame"> The video frame to submit. </param>
        /// <returns> A <see cref="Task{LiveCameraResult}"/> representing the asynchronous API call,
        ///     and containing the faces returned by the API. </returns>
        private async Task<LiveCameraResult> FacesAnalysisFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);
            // Submit image to API. 
            var attrs = new List<FaceAttributeType> { FaceAttributeType.Smile,
                FaceAttributeType.HeadPose };
            var faces = await _faceClient.DetectAsync(jpg, returnFaceLandmarks:true,returnFaceAttributes: attrs);
            // Count the API call. 
            Properties.Settings.Default.FaceAPICallCount++;
            // Output. 
            return new LiveCameraResult { Faces = faces };
        }

        /// <summary> Function which submits a frame to the Emotion API. </summary>
        /// <param name="frame"> The video frame to submit. </param>
        /// <returns> A <see cref="Task{LiveCameraResult}"/> representing the asynchronous API call,
        ///     and containing the emotions returned by the API. </returns>
        private async Task<LiveCameraResult> EmotionAnalysisFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);
            // Submit image to API. 
            Emotion[] emotions = null;

            // See if we have local face detections for this image.
            var localFaces = (OpenCvSharp.Rect[])frame.UserData;
            if (localFaces != null)
            {
                // If we have local face detections, we can call the API with them. 
                // First, convert the OpenCvSharp rectangles. 
                var rects = localFaces.Select(
                    f => new Microsoft.ProjectOxford.Common.Rectangle
                    {
                        Left = f.Left,
                        Top = f.Top,
                        Width = f.Width,
                        Height = f.Height
                    });
                emotions = await _emotionClient.RecognizeAsync(jpg, rects.ToArray());
            }
            else
            {
                // If not, the API will do the face detection. 
                emotions = await _emotionClient.RecognizeAsync(jpg);
            }

            // Count the API call. 
            Properties.Settings.Default.EmotionAPICallCount++;
            // Output. 
            return new LiveCameraResult
            {
                Faces = emotions.Select(e => CreateFace(e.FaceRectangle)).ToArray(),
                // Extract emotion scores from results. 
                EmotionScores = emotions.Select(e => e.Scores).ToArray()
            };
        }

        /// <summary> Function which submits a frame to the Computer Vision API for tagging. </summary>
        /// <param name="frame"> The video frame to submit. </param>
        /// <returns> A <see cref="Task{LiveCameraResult}"/> representing the asynchronous API call,
        ///     and containing the tags returned by the API. </returns>
        private async Task<LiveCameraResult> TaggingAnalysisFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);
            // Submit image to API. 
            var analysis = await _visionClient.GetTagsAsync(jpg);
            // Count the API call. 
            Properties.Settings.Default.VisionAPICallCount++;
            // Output. 
            return new LiveCameraResult { Tags = analysis.Tags };
        }

        /// <summary> Function which submits a frame to the Computer Vision API for celebrity
        ///     detection. </summary>
        /// <param name="frame"> The video frame to submit. </param>
        /// <returns> A <see cref="Task{LiveCameraResult}"/> representing the asynchronous API call,
        ///     and containing the celebrities returned by the API. </returns>
        private async Task<LiveCameraResult> CelebrityAnalysisFunction(VideoFrame frame)
        {
            // Encode image. 
            var jpg = frame.Image.ToMemoryStream(".jpg", s_jpegParams);
            // Submit image to API. 
            var result = await _visionClient.AnalyzeImageInDomainAsync(jpg, "celebrities");
            // Count the API call. 
            Properties.Settings.Default.VisionAPICallCount++;
            // Output. 
            var celebs = JsonConvert.DeserializeObject<CelebritiesResult>(result.Result.ToString()).Celebrities;
            return new LiveCameraResult
            {
                // Extract face rectangles from results. 
                Faces = celebs.Select(c => CreateFace(c.FaceRectangle)).ToArray(),
                // Extract celebrity names from results. 
                CelebrityNames = celebs.Select(c => c.Name).ToArray()
            };
        }

        private BitmapSource VisualizeResult(VideoFrame frame)
        {
            // Draw any results on top of the image. 
            BitmapSource visImage = frame.Image.ToBitmapSource();

            var result = _latestResultsToDisplay;

            if (result != null)
            {
                // See if we have local face detections for this image.
                var clientFaces = (OpenCvSharp.Rect[])frame.UserData;
                if (clientFaces != null && result.Faces != null)
                {
                    // If so, then the analysis results might be from an older frame. We need to match
                    // the client-side face detections (computed on this frame) with the analysis
                    // results (computed on the older frame) that we want to display. 
                    MatchAndReplaceFaceRectangles(result.Faces, clientFaces);
                }

                visImage = Visualization.DrawFaces(visImage, result.Faces, result.EmotionScores, result.CelebrityNames);
                visImage = Visualization.DrawTags(visImage, result.Tags);
            }

            return visImage;
        }

        /// <summary> Populate CameraList in the UI, once it is loaded. </summary>
        /// <param name="sender"> Source of the event. </param>
        /// <param name="e">      Routed event information. </param>
        private void CameraList_Loaded(object sender, RoutedEventArgs e)
        {
            int numCameras = _grabber.GetNumCameras();

            if (numCameras == 0)
            {
                MessageArea.Text = "No cameras found!";
            }

            var comboBox = sender as ComboBox;
            comboBox.ItemsSource = Enumerable.Range(0, numCameras).Select(i => string.Format("Camera {0}", i + 1));
            comboBox.SelectedIndex = 0;
        }

        /// <summary> Populate ModeList in the UI, once it is loaded. </summary>
        /// <param name="sender"> Source of the event. </param>
        /// <param name="e">      Routed event information. </param>
        private void ModeList_Loaded(object sender, RoutedEventArgs e)
        {
            var modes = (AppMode[])Enum.GetValues(typeof(AppMode));

            var comboBox = sender as ComboBox;
            comboBox.ItemsSource = modes.Select(m => m.ToString());
            comboBox.SelectedIndex = 0;
        }

        private void ModeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Disable "most-recent" results display. 
            _fuseClientRemoteResults = false;

            var comboBox = sender as ComboBox;
            var modes = (AppMode[])Enum.GetValues(typeof(AppMode));
            _mode = modes[comboBox.SelectedIndex];
            switch (_mode)
            {
                case AppMode.Faces:
                    _grabber.AnalysisFunction = FacesAnalysisFunction;
                    break;
                case AppMode.Emotions:
                    _grabber.AnalysisFunction = EmotionAnalysisFunction;
                    break;
                case AppMode.EmotionsWithClientFaceDetect:
                    // Same as Emotions, except we will display the most recent faces combined with
                    // the most recent API results. 
                    _grabber.AnalysisFunction = EmotionAnalysisFunction;
                    _fuseClientRemoteResults = true;
                    break;
                case AppMode.Tags:
                    _grabber.AnalysisFunction = TaggingAnalysisFunction;
                    break;
                case AppMode.Celebrities:
                    _grabber.AnalysisFunction = CelebrityAnalysisFunction;
                    break;
                default:
                    _grabber.AnalysisFunction = null;
                    break;
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (!CameraList.HasItems)
            {
                MessageArea.Text = "No cameras found; cannot start processing";
                return;
            }

            // Clean leading/trailing spaces in API keys. 
            Properties.Settings.Default.FaceAPIKey = Properties.Settings.Default.FaceAPIKey.Trim();
            Properties.Settings.Default.EmotionAPIKey = Properties.Settings.Default.EmotionAPIKey.Trim();
            Properties.Settings.Default.VisionAPIKey = Properties.Settings.Default.VisionAPIKey.Trim();

            // Create API clients. 
            _faceClient = new FaceServiceClient(Properties.Settings.Default.FaceAPIKey);
            _emotionClient = new EmotionServiceClient(Properties.Settings.Default.EmotionAPIKey);
            _visionClient = new VisionServiceClient(Properties.Settings.Default.VisionAPIKey);

            // How often to analyze. 
            _grabber.TriggerAnalysisOnInterval(Properties.Settings.Default.AnalysisInterval);

            // Reset message. 
            MessageArea.Text = "";

            // Record start time, for auto-stop
            _startTime = DateTime.Now;

            await _grabber.StartProcessingCameraAsync(CameraList.SelectedIndex);
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e)
        {
            await _grabber.StopProcessingAsync();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = 1 - SettingsPanel.Visibility;
        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsPanel.Visibility = Visibility.Hidden;
            Properties.Settings.Default.Save();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri));
            e.Handled = true;
        }

        private Face CreateFace(FaceRectangle rect)
        {
            return new Face
            {
                FaceRectangle = new FaceRectangle
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height
                }
            };
        }

        private Face CreateFace(Microsoft.ProjectOxford.Vision.Contract.FaceRectangle rect)
        {
            return new Face
            {
                FaceRectangle = new FaceRectangle
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height
                }
            };
        }

        private Face CreateFace(Microsoft.ProjectOxford.Common.Rectangle rect)
        {
            return new Face
            {
                FaceRectangle = new FaceRectangle
                {
                    Left = rect.Left,
                    Top = rect.Top,
                    Width = rect.Width,
                    Height = rect.Height
                }
            };
        }

        private void MatchAndReplaceFaceRectangles(Face[] faces, OpenCvSharp.Rect[] clientRects)
        {
            // Use a simple heuristic for matching the client-side faces to the faces in the
            // results. Just sort both lists left-to-right, and assume a 1:1 correspondence. 

            // Sort the faces left-to-right. 
            var sortedResultFaces = faces
                .OrderBy(f => f.FaceRectangle.Left + 0.5 * f.FaceRectangle.Width)
                .ToArray();

            // Sort the clientRects left-to-right.
            var sortedClientRects = clientRects
                .OrderBy(r => r.Left + 0.5 * r.Width)
                .ToArray();

            // Assume that the sorted lists now corrrespond directly. We can simply update the
            // FaceRectangles in sortedResultFaces, because they refer to the same underlying
            // objects as the input "faces" array. 
            for (int i = 0; i < Math.Min(faces.Length, clientRects.Length); i++)
            {
                // convert from OpenCvSharp rectangles
                OpenCvSharp.Rect r = sortedClientRects[i];
                sortedResultFaces[i].FaceRectangle = new FaceRectangle { Left = r.Left, Top = r.Top, Width = r.Width, Height = r.Height };
            }
        }

        private bool IsStill(VideoFrame currFrame)
        {
            if (_preFrame == null || _preResults == null)
            {
                _preFrame = currFrame;
                _preResults = _latestResultsToDisplay;    
                return false;
            }

            LiveCameraResult currResults = _latestResultsToDisplay;
            //Mat currMat = currFrame.Image;
            //var currIndexer = currMat.GetGenericIndexer<Vec3b>();

            //Mat preMat = _preFrame.Image;
            //var preIndexer = preMat.GetGenericIndexer<Vec3d>();
            LiveCameraResult preResults = _preResults;

            double avr = 0;

            if (currResults.Faces.Length != preResults.Faces.Length)
            {
                _preFrame = currFrame;
                _preResults = currResults;
                return false;
            }

            for (int i = 0; i < currResults.Faces.Length; i++)
            {
                avr = 0;
                Face currFaceResult = currResults.Faces[i];
                Face preFaceResult = preResults.Faces[i];
                avr += Math.Abs(currFaceResult.FaceRectangle.Left - preFaceResult.FaceRectangle.Left);
                avr += Math.Abs(currFaceResult.FaceRectangle.Top - preFaceResult.FaceRectangle.Top);
                if (avr > 20)
                {
                    _preFrame = currFrame;
                    _preResults = currResults;
                    return false;
                }
            }

            avr /= currResults.Faces.Length;
            /*
            for (int y = 0; y < currMat.Height; y++)
            {
                for (int x = 0; x < currMat.Width; x++)
                {
                    Vec3b color = currIndexer[y, x];
                    byte temp = color.Item0;
                    color.Item0 = color.Item2; // B <- R
                    color.Item2 = temp;        // R <- B
                    currIndexer[y, x] = color;
                }
            }
            */
            _preFrame = currFrame;
            _preResults = currResults;
            return true;
            //MessageArea.Text = avr.ToString();
        }

        private bool IsAllFaceCamera()
        {
            LiveCameraResult currResults = _latestResultsToDisplay;
            int numFaces = currResults.Faces.Length;
            for (int i = 0; i < numFaces; i++)
            {
                Face face = currResults.Faces[i];
                if (Math.Abs(face.FaceAttributes.HeadPose.Yaw) > 25)
                    return false;
            }
            return true;
        }

        private bool HasPeople()
        {
            LiveCameraResult currResults = _latestResultsToDisplay;
            if (currResults.Faces.Length <= 0)
            {
                return false;
            }
            else
            {
                return true;
            }
        }

        private bool IsAllHappiness()
        {

            bool isAllHappiness = true;
            LiveCameraResult lcr = _latestResultsToDisplay;
            //        MessageArea.Text = lcr.Faces[0].FaceAttributes.Smile.ToString();
            for (int i = 0; i < lcr.Faces.Length; i++)
            {
                if (lcr.Faces[i].FaceAttributes.Smile <= 0.5)
                {
                    isAllHappiness = false;
                }
            }

            //MessageArea.Text = isAllHappiness.ToString();
            return isAllHappiness;
        }

        private bool NoEyeClosed()
        {
            double EYE_THRESHOLD = 0.15;
            LiveCameraResult currResults = _latestResultsToDisplay;
            bool flag = true;
            int numFaces = currResults.Faces.Length;
            for (int i = 0; i < numFaces; i++)
            {
                Face face = currResults.Faces[i];
                double left_eye_hight = Math.Abs(face.FaceLandmarks.EyeLeftBottom.Y - face.FaceLandmarks.EyeLeftTop.Y);
                double left_eye_width = Math.Abs(face.FaceLandmarks.EyeLeftInner.X - face.FaceLandmarks.EyeLeftOuter.X);

                double right_eye_hight = Math.Abs(face.FaceLandmarks.EyeRightBottom.Y - face.FaceLandmarks.EyeRightTop.Y);
                double right_eye_width = Math.Abs(face.FaceLandmarks.EyeRightInner.X - face.FaceLandmarks.EyeRightOuter.X);

                double left_eye = left_eye_hight / left_eye_width;
                double right_eye = right_eye_hight / right_eye_width;
                if (left_eye < EYE_THRESHOLD && right_eye < EYE_THRESHOLD)
                {
                    flag = false;
                }
            }
            return flag;
        }
    }
}
