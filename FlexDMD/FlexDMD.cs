﻿/* Copyright 2019 Vincent Bousquet

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
   */
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using static FlexDMD.DMDDevice;
using NAudio.MediaFoundation;
using System.Reflection;
using System.IO;
using NLog.Config;
using System.Text.RegularExpressions;

namespace FlexDMD
{
    [Guid("766e10d3-dfe3-4e1b-ac99-c4d2be16e91f"), ComVisible(true), ClassInterface(ClassInterfaceType.None), ComSourceInterfaces(typeof(IDMDObjectEvents))]
    public class FlexDMD : IFlexDMD
    {
        private int _runtimeVersion = 1008; // 1009 (1.9) is the first version introducing a breaking change. The default is to be backward compatible unless otherwise requested by the user
        private static readonly Logger log = LogManager.GetCurrentClassLogger();
        private readonly List<System.Action> _runnables = new List<System.Action>();
        private readonly Group _stage;
        private readonly int _frameRate = 60;
        private readonly Mutex _renderMutex = new Mutex();
        private int _renderLockCount = 0;
        private DMDDevice _dmdDevice = null, _dmdScreen = null;
        private Thread _processThread = null;
        private bool _run = false;
        private bool _show = true;
        private ushort _width = 128;
        private ushort _height = 32;
        private string _gameName = "";
        private Bitmap _frame = null;
        private object[] _pixels = null;
        private object[] _coloredPixels = null;
        private RenderMode _renderMode = RenderMode.DMD_GRAY_4;
        private Color _dmdColor = Color.FromArgb(0xFF, 0x58, 0x20);
        private IntPtr _segData1 = Marshal.AllocHGlobal(128);
        private IntPtr _segData2 = Marshal.AllocHGlobal(128);

        public event OnDMDChangedDelegate OnDMDChanged;
        public delegate void OnDMDChangedDelegate();

        public IGroupActor Stage { get => _stage; }
        public IGroupActor NewGroup(string name) { var g = new Group(this) { Name = name }; return g; }
        public IFrameActor NewFrame(string name) { var g = new Frame() { Name = name }; return g; }
        public ILabelActor NewLabel(string name, Font font, string text) { var g = new Label(this, font, text) { Name = name }; return g; }
        public IVideoActor NewVideo(string name, string path)
        {
            try
            {
                if (path.Contains("|"))
                    return new ImageSequence(AssetManager, path, name);
                else
                {
                    var src = AssetManager.ResolveSrc(path);
                    if (src.AssetType == AssetType.Video)
                        return new Video(src.Path, name);
                    if (src.AssetType == AssetType.Gif)
                        return new GIFImage(AssetManager, path, name);
                    if (src.AssetType == AssetType.Image)
                        return new ImageSequence(AssetManager, path, name);
                    if (src.AssetType == AssetType.APNG) // Añadido soporte para APNG
                        return new APNGImage(AssetManager, path, name);
                }
            }
            catch (Exception ex)
            {
                log.Error(ex, "Failed to create video actor for path: {0}", path);
                // Silently discard loading exception and returns null to caller
            }
            return null;
        }
        public IImageActor NewImage(string name, string image) => new Image(AssetManager, image, name);
        public Font NewFont(string font, Color tint, Color borderTint, int borderSize) => AssetManager.GetFont(AssetManager.ResolveSrc(string.Format("{0}&tint={1:X8}&border_size={2}&border_tint={3:X8}", font, tint.ToArgb(), borderSize, borderTint.ToArgb())));
        public IUltraDMD NewUltraDMD() => new UltraDMD.UltraDMD(this);

        public Graphics Graphics { get; private set; } = null;

        public int Version
        {
            get
            {
                var fvi = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location);
                return fvi.FileMajorPart * 1000 + fvi.FileMinorPart;
            }
        }

        public int RuntimeVersion
        {
            get => _runtimeVersion;
            set => _runtimeVersion = value;
        }

        public bool Run
        {
            get => _run;
            set
            {
                if (_run == value) return;
                _run = value;
                if (_run)
                {
                    MediaFoundationApi.Startup();
                    ShowDMD(_show);
                    log.Info("Starting render thread for game '{0}' using render mode {1}", _gameName, _renderMode);
                    if (_renderMode == RenderMode.DMD_GRAY_2 || _renderMode == RenderMode.DMD_GRAY_4 || _renderMode == RenderMode.DMD_RGB)
                    {
                        _processThread = new Thread(new ThreadStart(RenderLoop)) { IsBackground = true };
                        _processThread.Start();
                    }
                }
                else
                {
                    log.Info("Stopping render thread");
                    if (Thread.CurrentThread != _processThread) _processThread?.Join();
                    _processThread = null;
                    ShowDMD(false);
                    MediaFoundationApi.Shutdown();
                    AssetManager.ClearAll();
                }
            }
        }

        public bool Show
        {
            get => _show;
            set
            {
                if (_show == value) return;
                _show = value;
                if (_run) ShowDMD(_show);
            }
        }

        private void ShowDMD(bool show)
        {
            LockRenderThread();
            if (show && _dmdDevice == null)
            {
                log.Info("Show DMD");
                var options = new PMoptions
                {
                    Red = _dmdColor.R,
                    Green = _dmdColor.G,
                    Blue = _dmdColor.B,
                    Perc66 = 66,
                    Perc33 = 33,
                    Perc0 = 0
                };
                options.Green0 = options.Perc0 * options.Green / 100;
                options.Green33 = options.Perc33 * options.Green / 100;
                options.Green66 = options.Perc66 * options.Green / 100;
                options.Blue0 = options.Perc0 * options.Blue / 100;
                options.Blue33 = options.Perc33 * options.Blue / 100;
                options.Blue66 = options.Perc66 * options.Blue / 100;
                options.Red0 = options.Perc0 * options.Red / 100;
                options.Red33 = options.Perc33 * options.Red / 100;
                options.Red66 = options.Perc66 * options.Red / 100;
                if (_gameName == "")
                {
                    WindowHandle editor = WindowHandle.FindWindow(wh => wh.GetClassName().Equals("VPinball") && wh.GetWindowText().StartsWith("Visual Pinball - ["));
                    if (editor != null)
                    {
                        // Used filename taken from window title, removing version pattern:
                        // - First separator: space, _, v, V
                        // - Then version: [0..9], ., _
                        // - Then subversion as letter: optional final char
                        // - Then optional qualifier: optional appended "-DOF"
                        // - Then optional * marker (modified in editor)
                        // - Then end of name
                        string title = editor.GetWindowText();
                        int trailer = "Visual Pinball - [".Length;
                        _gameName = title.Substring(trailer, title.Length - trailer - 1).Trim();
                        _gameName = new Regex("[\\s_vV][\\d_\\.]+[a-z]?(-DOF)?\\*?$").Replace(_gameName, "").Trim();
                        log.Info("GameName was not set, using file name instead: '" + _gameName + "'");
                    }
                }
                _dmdDevice = new DMDDevice("dmddevice");
                _dmdDevice.Open();
                _dmdDevice.GameSettings(_gameName, 0, options);
                _dmdScreen = new DMDDevice("dmdscreen", false);
                _dmdScreen.Open();
                _dmdScreen.GameSettings(_gameName, 0, options);
            }
            else if (!show && _dmdDevice != null)
            {
                log.Info("Hide DMD");
                _dmdDevice.Close();
                _dmdDevice.Dispose();
                _dmdDevice = null;
                _dmdScreen.Close();
                _dmdScreen.Dispose();
                _dmdScreen = null;
            }
            UnlockRenderThread();
        }

        public string GameName
        {
            get => _gameName;
            set
            {
                if (value == null)
                {
                    GameName = "";
                    return;
                }
                if (_gameName == value) return;
                bool wasVisible = Show;
                Show = false;
                log.Info("Game name set to {0}", value);
                _gameName = value;
                Show = wasVisible;
            }
        }

        public ushort Width
        {
            get => _width;
            set
            {
                if (_width < 1) return;
                if (_width == value) return;
                bool wasVisible = Show;
                Show = false;
                _width = value;
                Show = wasVisible;
            }
        }

        public ushort Height
        {
            get => _height;
            set
            {
                if (_height < 1) return;
                if (_height == value) return;
                bool wasVisible = Show;
                Show = false;
                _height = value;
                Show = wasVisible;
            }
        }

        public Color Color
        {
            get => _dmdColor;
            set
            {
                if (_dmdColor == value) return;
                bool wasVisible = Show;
                Show = false;
                _dmdColor = value;
                Show = wasVisible;
            }
        }

        public RenderMode RenderMode
        {
            get => _renderMode;
            set
            {
                if (_renderMode == value) return;
                bool wasRunning = Show;
                Show = false;
                log.Info("Render mode set to {0}", value);
                _renderMode = value;
                Show = wasRunning;
            }
        }

        public string ProjectFolder
        {
            get => AssetManager.BasePath;
            set
            {
                log.Info("SetProjectFolder {0}", value);
                AssetManager.BasePath = value;
            }
        }

        public string TableFile
        {
            get => AssetManager.TableFile;
            set
            {
                log.Info("Table File defined to {0}", value);
                AssetManager.TableFile = value;
            }
        }

        public bool Clear { get; set; } = false;

        public object DmdColoredPixels
        {
            get
            {
                if (_coloredPixels == null || _coloredPixels.Length != _width * _height) _coloredPixels = new object[_width * _height];
                return _coloredPixels;
            }
        }

        public object DmdPixels
        {
            get
            {
                if (_pixels == null || _pixels.Length != _width * _height) _pixels = new object[_width * _height];
                return _pixels;
            }
        }

        public object Segments
        {
            set
            {
                if (!Run || !Show)
                    return;
                int Size1 = 0, Size2 = 0;
                switch (_renderMode)
                {
                    case RenderMode.SEG_2x16Alpha:
                        Size1 = 2 * 16;
                        break;
                    case RenderMode.SEG_2x20Alpha:
                        Size1 = 2 * 20;
                        break;
                    case RenderMode.SEG_2x7Alpha_2x7Num:
                        Size1 = 2 * 7 + 2 * 7;
                        break;
                    case RenderMode.SEG_2x7Alpha_2x7Num_4x1Num:
                        Size1 = 2 * 7 + 2 * 7 + 4;
                        break;
                    case RenderMode.SEG_2x7Num_2x7Num_4x1Num:
                        Size1 = 2 * 7 + 2 * 7 + 4;
                        break;
                    case RenderMode.SEG_2x7Num_2x7Num_10x1Num:
                        Size1 = 2 * 7 + 2 * 7 + 4;
                        Size2 = 6;
                        break;
                    case RenderMode.SEG_2x7Num_2x7Num_4x1Num_gen7:
                        Size1 = 2 * 7 + 2 * 7 + 4;
                        break;
                    case RenderMode.SEG_2x7Num10_2x7Num10_4x1Num:
                        Size1 = 2 * 7 + 2 * 7 + 4;
                        break;
                    case RenderMode.SEG_2x6Num_2x6Num_4x1Num:
                        Size1 = 2 * 6 + 2 * 6 + 4;
                        break;
                    case RenderMode.SEG_2x6Num10_2x6Num10_4x1Num:
                        Size1 = 2 * 6 + 2 * 6 + 4;
                        break;
                    case RenderMode.SEG_4x7Num10:
                        Size1 = 4 * 7;
                        break;
                    case RenderMode.SEG_6x4Num_4x1Num:
                        Size1 = 6 * 4 + 4;
                        break;
                    case RenderMode.SEG_2x7Num_4x1Num_1x16Alpha:
                        Size1 = 2 * 7 + 4 + 1;
                        break;
                    case RenderMode.SEG_1x16Alpha_1x16Num_1x7Num:
                        Size1 = 1 + 1 + 7;
                        break;
                }
                unsafe
                {
                    short* seg1 = (short*)_segData1.ToPointer();
                    for (int i = 0; i < Size1; i++)
                    {
                        seg1[i] = (short)((object[])value)[i];
                    }
                    short* seg2 = (short*)_segData2.ToPointer();
                    for (int i = 0; i < Size2; i++)
                    {
                        seg2[i] = (short)((object[])value)[Size1 + i];
                    }
                }
                _dmdDevice.RenderAlphaNumeric(NumericalLayout.__2x16Alpha, _segData1, _segData2);
                _dmdScreen.RenderAlphaNumeric(NumericalLayout.__2x16Alpha, _segData1, _segData2);
            }
        }

        public AssetManager AssetManager { get; } = new AssetManager();

        public FlexDMD()
        {
            var assembly = Assembly.GetCallingAssembly();
            var assemblyPath = Path.GetDirectoryName(new Uri(assembly.CodeBase).LocalPath);
            var logConfigPath = Path.Combine(assemblyPath, "FlexDMD.log.config");
            if (File.Exists(logConfigPath))
            {
                LogManager.ThrowConfigExceptions = false;
                LogManager.Configuration = new XmlLoggingConfiguration(logConfigPath);
                LogManager.ReconfigExistingLoggers();
            }
            log.Info("FlexDMD version {0}", assembly.GetName().Version);
            _stage = new Group(this) { Name = "Stage" };
        }

        ~FlexDMD()
        {
            if (_run)
            {
                log.Error("Destructor called before Uninit");
                Run = false;
            }
            if (_segData1 != IntPtr.Zero) Marshal.FreeHGlobal(_segData1);
            if (_segData2 != IntPtr.Zero) Marshal.FreeHGlobal(_segData2);
            _segData1 = IntPtr.Zero;
            _segData2 = IntPtr.Zero;
        }

        public void LockRenderThread()
        {
            _renderLockCount++;
            if (_renderLockCount == 1)
                _renderMutex.WaitOne();
        }

        public void UnlockRenderThread()
        {
            _renderLockCount--;
            if (_renderLockCount == 0)
                _renderMutex.ReleaseMutex();
        }

        public void Post(System.Action runnable)
        {
            lock (_runnables)
            {
                _runnables.Add(runnable);
            }
        }

        public void RenderLoop()
        {
            log.Info("RenderThread start");
            _frame = new Bitmap(_width, _height, PixelFormat.Format24bppRgb);
            Graphics = Graphics.FromImage(_frame);
            _stage.SetSize(_width, _height);
            _stage.OnStage = true;
            Stopwatch stopWatch = new Stopwatch();
            WindowHandle visualPinball = null;
            IntPtr _bpFrame = _renderMode != RenderMode.DMD_RGB ? Marshal.AllocHGlobal(_width * _height) : IntPtr.Zero;
            double elapsedMs = 0.0;
            while (Run)
            {
                stopWatch.Restart();
                if (visualPinball == null)
                {
                    visualPinball = WindowHandle.FindWindow(wh => wh.IsVisible() && wh.GetClassName().Equals("VPPlayer") && wh.GetWindowText().StartsWith("Visual Pinball Player"));
                    if (visualPinball != null)
                        log.Info("Attaching to Visual Pinball Player window lifecycle");
                }
                else if (!visualPinball.IsWindow())
                {
                    log.Info("Closing FlexDMD since Visual Pinball Player window was closed");
                    Run = false;
                    break;
                }
                if (Clear) Graphics.Clear(Color.Black);
                _renderMutex.WaitOne();
                lock (_runnables)
                {
                    _runnables.ForEach(item => item());
                    _runnables.Clear();
                }
                _stage.Update((float)(elapsedMs / 1000.0));
                _stage.Draw(Graphics);
                _renderMutex.ReleaseMutex();
                Rectangle rect = new Rectangle(0, 0, _frame.Width, _frame.Height);
                BitmapData data = _frame.LockBits(rect, ImageLockMode.ReadWrite, _frame.PixelFormat);
                switch (_renderMode)
                {
                    case RenderMode.DMD_GRAY_2:
                        unsafe
                        {
                            byte* dst = (byte*)_bpFrame.ToPointer();
                            byte* ptr = ((byte*)data.Scan0.ToPointer());
                            int pos = 0;
                            for (int y = 0; y < _height; y++)
                            {
                                for (int x = 0; x < _width; x++)
                                {
                                    byte r = *ptr;
                                    ptr++;
                                    byte g = *ptr;
                                    ptr++;
                                    byte b = *ptr;
                                    ptr++;
                                    int v = (int)(0.2126f * r + 0.7152f * g + 0.0722f * b);
                                    if (v > 255) v = 255;
                                    dst[pos] = (byte)(v >> 6);
                                    if (_pixels != null) _pixels[pos] = (byte)(v);
                                    pos++;
                                }
                                ptr += data.Stride - 3 * _width;
                            }
                        }
                        try
                        {
                            _dmdDevice?.RenderGray2(_width, _height, _bpFrame);
                            _dmdScreen?.RenderGray2(_width, _height, _bpFrame);
                        }
                        catch (Exception) { }
                        break;

                    case RenderMode.DMD_GRAY_4:
                        unsafe
                        {
                            byte* dst = (byte*)_bpFrame.ToPointer();
                            byte* ptr = ((byte*)data.Scan0.ToPointer());
                            int pos = 0;
                            for (int y = 0; y < _height; y++)
                            {
                                for (int x = 0; x < _width; x++)
                                {
                                    byte r = *ptr;
                                    ptr++;
                                    byte g = *ptr;
                                    ptr++;
                                    byte b = *ptr;
                                    ptr++;
                                    int v = (int)(0.2126f * r + 0.7152f * g + 0.0722f * b);
                                    if (v > 255) v = 255;
                                    dst[pos] = (byte)(v >> 4);
                                    if (_pixels != null) _pixels[pos] = (byte)(v);
                                    pos++;
                                }
                                ptr += data.Stride - 3 * _width;
                            }
                        }
                        try
                        {
                            _dmdDevice?.RenderGray4(_width, _height, _bpFrame);
                            _dmdScreen?.RenderGray4(_width, _height, _bpFrame);
                        }
                        catch (Exception) { }
                        break;

                    case RenderMode.DMD_RGB:
                        if (_pixels != null)
                        {
                            unsafe
                            {
                                byte* ptr = ((byte*)data.Scan0.ToPointer());
                                int pos = 0;
                                for (int y = 0; y < _height; y++)
                                {
                                    for (int x = 0; x < _width; x++)
                                    {
                                        byte r = *ptr;
                                        ptr++;
                                        byte g = *ptr;
                                        ptr++;
                                        byte b = *ptr;
                                        ptr++;
                                        float v = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                                        if (v > 255.0f) v = 255.0f;
                                        _pixels[pos] = (byte)(v);
                                        pos++;
                                    }
                                }
                            }
                        }
                        try
                        {
                            _dmdDevice?.RenderRgb24(_width, _height, data.Scan0);
                            _dmdScreen?.RenderRgb24(_width, _height, data.Scan0);
                        }
                        catch (Exception) { }
                        break;
                }

                if (_coloredPixels != null)
                {
                    unsafe
                    {
                        byte* ptr = ((byte*)data.Scan0.ToPointer());
                        int pos = 0;
                        for (int y = 0; y < _height; y++)
                        {
                            for (int x = 0; x < _width; x++)
                            {
                                byte r = *ptr;
                                ptr++;
                                byte g = *ptr;
                                ptr++;
                                byte b = *ptr;
                                ptr++;
                                _coloredPixels[pos] = (uint)((b << 16) + (g << 8) + r);
                                pos++;
                            }
                        }
                    }
                }
                _frame.UnlockBits(data);
                OnDMDChanged?.Invoke();
                double renderingDuration = stopWatch.Elapsed.TotalMilliseconds;

                int sleepMs = (1000 / _frameRate) - (int)renderingDuration;
                if (sleepMs > 1) Thread.Sleep(sleepMs);
                elapsedMs = stopWatch.Elapsed.TotalMilliseconds;
                if (elapsedMs > 4000 / _frameRate)
                {
                    log.Warn("Abnormally long elapsed time between frames of {0}s (rendering lasted {1}ms, sleeping was {2}ms), limiting to {3}ms", elapsedMs / 1000.0, renderingDuration, Math.Max(0, sleepMs), 4000 / _frameRate);
                    elapsedMs = 4000 / _frameRate;
                }
            }
            _stage.OnStage = false;
            if (_bpFrame != IntPtr.Zero) Marshal.FreeHGlobal(_bpFrame);
            Graphics.Dispose();
            Graphics = null;
            _frame.Dispose();
            _frame = null;
            _processThread = null;
            log.Info("RenderThread end");
        }
    }
}
