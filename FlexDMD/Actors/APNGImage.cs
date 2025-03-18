﻿/* Copyright 2025 Gustavo A. Lara

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

//APNG conversion needs ffmpeg installed in the system or where flexdmd.dll is located 

using FlexDMD.Actors;
using NLog;
using ImageMagick;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Drawing.Imaging;

namespace FlexDMD
{
    class APNGImage : AnimatedActor
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();
        private readonly AssetSrc _src;
        private readonly AssetManager _manager;
        private readonly float[] _frameDelays;
        private readonly float _length;
        private List<MagickImage> _frames = null;
        private int _pos = 0;
        private Bitmap _currentFrame = null;
        private readonly object _frameLock = new object();
        private float _canvasWidth, _canvasHeight;
        private float _prefWidth, _prefHeight; // Añadido aquí

        public APNGImage(AssetManager manager, string path, string name = "")
        {
            
            _src = manager.ResolveSrc(path) ?? throw new ArgumentNullException(nameof(_src), "Source cannot be null");
            _manager = manager ?? throw new ArgumentNullException(nameof(manager), "Manager cannot be null");
            Name = name;

            string fullPath = _src.Path;
            log.Info($"APNGImage: Path: {fullPath}");
            try
            {
                var apng = new MagickImageCollection(@"apng:" + fullPath);
                
                int frameCount = apng.Count;
                log.Info($"APNG frame count: {frameCount}");

                if (frameCount == 0)
                {
                    throw new InvalidOperationException("The provided image has no frames.");
                }

                _frames = new List<MagickImage>();
                _frameDelays = new float[frameCount];

                for (int i = 0; i < frameCount; i++)
                {
                    var frame = (MagickImage)apng[i].Clone();
                    _frames.Add(frame);
                    _frameDelays[i] = frame.AnimationDelay / 100f;
                    log.Info($"Frame {i} delay: {_frameDelays[i]}");
                }

                _canvasWidth = _frames[0].BaseWidth;
                _canvasHeight = _frames[0].BaseHeight;

                _prefWidth = _canvasWidth; 
                _prefHeight = _canvasHeight; 

                _length = _frameDelays.Sum();

                log.Info("APNGImage initialized with {0} frames, PrefWidth: {1}, PrefHeight: {2}, Length: {3}", frameCount, _prefWidth, _prefHeight, _length);
            }
            catch (Exception ex)
            {
                log.Error($"Error loading APNG: {ex.Message}");
            }
            log.Info($"APNGImage: frames: {_frames.Count}, frameDelays: {_frameDelays.Length}");
            Rewind();
            Pack();
            UpdateFrame();
        }

        public override float PrefWidth { get => _prefWidth; } 
        public override float PrefHeight { get => _prefHeight; } 
        public override float Length { get => _length; }

        protected override void Rewind()
        {
            base.Rewind();
            lock (_frameLock)
            {
                _pos = 0;
                _frameDuration = _frameDelays[0];
            }
        }

        protected override void ReadNextFrame()
        {
            log.Info("ReadNextFrame. Pos: {0}, FrameDelays.Length: {1}, EndOfAnimation: {2}", _pos, _frameDelays.Length, _endOfAnimation);
            lock (_frameLock)
            {
                
                    if (_pos >= _frameDelays.Length - 1)
                    {
                        log.Info("Detectado el final de la animación.");
                        _endOfAnimation = true;
    
                    }
                    else
                    {
                        _pos++;
                        _frameTime = 0;
                        for (int i = 0; i < _pos; i++)
                            _frameTime += _frameDelays[i];
                        _frameDuration = _frameDelays[_pos];
                        UpdateFrame();
                    }

              
            }
        }

        private void UpdateFrame()
        {
            log.Info($"UpdateFrame: pos: {_pos}");
            lock (_frameLock)
            {
                if (_frames != null && _frames.Count > 0)
                {
                    try
                    {
                        _frames[_pos].ColorSpace = ColorSpace.sRGB;
                        byte[] imageData = _frames[_pos].ToByteArray(MagickFormat.Rgba);
                        int stride = (int)_frames[_pos].Width * 4; // 4 bytes por píxel (RGBA)
                        Bitmap frameBitmap = new Bitmap((int)_frames[_pos].Width, (int)_frames[_pos].Height, stride, PixelFormat.Format32bppArgb, System.Runtime.InteropServices.Marshal.UnsafeAddrOfPinnedArrayElement(imageData, 0));
                        Bitmap canvasBitmap = new Bitmap((int)_canvasWidth, (int)_canvasHeight, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(canvasBitmap))
                        {
                            g.DrawImage(frameBitmap, _frames[_pos].Page.X, _frames[_pos].Page.Y);
                        }
                        _currentFrame = canvasBitmap;
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex, "Error converting MagickImage to Bitmap using ToByteArray()");
                    }
                }
            }
        }

        public override void Draw(Graphics graphics)
        {
            lock (_frameLock)
            {
                if (Visible && _currentFrame != null)


                    try
                    {
                        // Crear un nuevo Bitmap y copiar los datos
                        Bitmap frameToDraw = new Bitmap(_currentFrame.Width, _currentFrame.Height, _currentFrame.PixelFormat);
                        using (Graphics tempGraphics = Graphics.FromImage(frameToDraw))
                        {
                            tempGraphics.DrawImage(_currentFrame, 0, 0);
                        }

                        Layout.Scale(Scaling, PrefWidth, PrefHeight, Width, Height, out float w, out float h);
                        Layout.Align(Alignment, w, h, Width, Height, out float x, out float y);
                        graphics.DrawImage(frameToDraw, (int)(X + x), (int)(Y + y), (int)w, (int)h);

                        frameToDraw.Dispose(); // Free the new Bitmap
                    }
                    catch (Exception ex)
                    {
                        log.Error(ex, "Error drawing APNG frame.");
                    }
            }
        }

    }

}
