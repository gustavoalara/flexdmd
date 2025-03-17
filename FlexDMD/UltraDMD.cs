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
using FlexDMD;
using FlexDMD.Actors;
using FlexDMD.Scenes;
using Microsoft.Win32;
using NLog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;

// FIXME use the DMD color as the tint for all fonts
namespace UltraDMD
{
    public class VideoDef
    {
        public string VideoFilename { get; set; } = "";
        public Scaling Scaling { get; set; } = Scaling.Stretch;
        public Alignment Alignment { get; set; } = Alignment.Center;
        public bool Loop { get; set; } = false;

        public override bool Equals(object obj)
        {
            return obj is VideoDef def &&
                   VideoFilename == def.VideoFilename &&
                   Scaling == def.Scaling &&
                   Alignment == def.Alignment &&
                   Loop == def.Loop;
        }

        public override int GetHashCode()
        {
            var hashCode = 96768724;
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(VideoFilename);
            hashCode = hashCode * -1521134295 + Scaling.GetHashCode();
            hashCode = hashCode * -1521134295 + Alignment.GetHashCode();
            hashCode = hashCode * -1521134295 + Loop.GetHashCode();
            return hashCode;
        }
    }

    public class ImageSequenceDef
    {
        public string _images;
        public Scaling Scaling { get; set; } = Scaling.Stretch;
        public Alignment Alignment { get; set; } = Alignment.Center;
        public int Fps { get; set; } = 25;
        public bool Loop { get; set; } = true;

        public ImageSequenceDef(string images, int fps, bool loop)
        {
            _images = images;
            Fps = fps;
            Loop = loop;
        }

        public override bool Equals(object obj)
        {
            return obj is ImageSequenceDef def &&
                   _images == def._images &&
                   Fps == def.Fps &&
                   Loop == def.Loop;
        }

        public override int GetHashCode()
        {
            var hashCode = -2035125405;
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(_images);
            hashCode = hashCode * -1521134295 + Fps.GetHashCode();
            hashCode = hashCode * -1521134295 + Loop.GetHashCode();
            return hashCode;
        }
    }

    public class FontDef
    {
        public Color Tint { get; set; } = Color.White;
        public Color BorderTint { get; set; } = Color.White;
        public int BorderSize { get; set; } = 0;
        public string Path { get; set; } = "";

        public FontDef(string path, Color tint, Color borderTint, int borderSize = 0)
        {
            Path = path;
            Tint = tint;
            BorderTint = borderTint;
            BorderSize = borderSize;
        }

        public override bool Equals(object obj)
        {
            return obj is FontDef def &&
                   Tint.R == def.Tint.R &&
                   Tint.G == def.Tint.G &&
                   Tint.B == def.Tint.B &&
                   Tint.A == def.Tint.A &&
                   BorderTint.R == def.BorderTint.R &&
                   BorderTint.G == def.BorderTint.G &&
                   BorderTint.B == def.BorderTint.B &&
                   BorderTint.A == def.BorderTint.A &&
                   BorderSize == def.BorderSize &&
                   Path.Equals(def.Path);
        }

        public override int GetHashCode()
        {
            var hashCode = -1876634251;
            hashCode = hashCode * -1521134295 + Tint.R;
            hashCode = hashCode * -1521134295 + Tint.G;
            hashCode = hashCode * -1521134295 + Tint.B;
            hashCode = hashCode * -1521134295 + Tint.A;
            hashCode = hashCode * -1521134295 + BorderTint.R;
            hashCode = hashCode * -1521134295 + BorderTint.G;
            hashCode = hashCode * -1521134295 + BorderTint.B;
            hashCode = hashCode * -1521134295 + BorderTint.A;
            hashCode = hashCode * -1521134295 + BorderSize;
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(Path);
            return hashCode;
        }

        public override string ToString()
        {
            return string.Format("FontDef [path={0}, tint={1}, border tint={2}, border size={3}]", Path, Tint, BorderTint, BorderSize);
        }

    }

    /// <summary>
    /// Implementation of the UltraDMD API using the FlexDMD rendering engine.
    /// </summary>
    public class UltraDMD : IUltraDMD
    {
        private static readonly Logger log = LogManager.GetCurrentClassLogger();
        private readonly FlexDMD.FlexDMD _flexDMD;
        private readonly Sequence _queue;
        private readonly Dictionary<int, object> _preloads = new Dictionary<int, object>();
        private readonly ScoreBoard _scoreBoard;
        private readonly FontDef _scoreFontText;
        private readonly FontDef _scoreFontNormal;
        private readonly FontDef _scoreFontHighlight;
        private readonly FontDef _twoLinesFontTop;
        private readonly FontDef _twoLinesFontBottom;
        private readonly FontDef[] _singleLineFont;
        private bool _visible = true;
        private int _stretchMode = 0;
        private int _nextId = 1;
        private const bool LOG_DEBUG = true;

        public UltraDMD(FlexDMD.FlexDMD flexDMD)
        {
            _flexDMD = flexDMD;
            _queue = new Sequence(_flexDMD);
            _queue.FillParent = true;
            // UltraDMD uses f4by5 / f5by7 / f6by12
            _scoreFontText = new FontDef("FlexDMD.Resources.udmd-f4by5.fnt", Color.FromArgb(168, 168, 168), Color.White);
            _scoreFontNormal = new FontDef("FlexDMD.Resources.udmd-f5by7.fnt", Color.FromArgb(168, 168, 168), Color.White);
            _scoreFontHighlight = new FontDef("FlexDMD.Resources.udmd-f6by12.fnt", Color.White, Color.White);
            // UltraDMD uses f14by26 or f12by24 or f7by13 to fit in
            _singleLineFont = new FontDef[] {
                new FontDef("FlexDMD.Resources.udmd-f14by26.fnt", Color.White, Color.White),
                new FontDef("FlexDMD.Resources.udmd-f12by24.fnt", Color.White, Color.White),
                new FontDef("FlexDMD.Resources.udmd-f7by13.fnt", Color.White, Color.White)
            };
            // UltraDMD uses f5by7 / f6by12 for top / bottom line
            _twoLinesFontTop = new FontDef("FlexDMD.Resources.udmd-f5by7.fnt", Color.White, Color.White);
            _twoLinesFontBottom = new FontDef("FlexDMD.Resources.udmd-f6by12.fnt", Color.White, Color.White);
            // Core rendering tree
            _scoreBoard = new ScoreBoard(
                _flexDMD,
                _flexDMD.NewFont(_scoreFontNormal.Path, _scoreFontNormal.Tint, _scoreFontNormal.BorderTint, _scoreFontNormal.BorderSize),
                _flexDMD.NewFont(_scoreFontHighlight.Path, _scoreFontHighlight.Tint, _scoreFontHighlight.BorderTint, _scoreFontHighlight.BorderSize),
                _flexDMD.NewFont(_scoreFontText.Path, _scoreFontText.Tint, _scoreFontText.BorderTint, _scoreFontText.BorderSize)
                )
            { Visible = false };
            _flexDMD.Stage.AddActor(_scoreBoard);
            _flexDMD.Stage.AddActor(_queue);
        }

        private Actor ResolveImage(string filename, bool useFrame)
        {
            try
            {
                if (int.TryParse(filename, out int preloadId) && _preloads.ContainsKey(preloadId))
                {
                    var preload = _preloads[preloadId];
                    if (preload is VideoDef vp)
                    {
                        var actor = _flexDMD.NewVideo("", vp.VideoFilename);
                        if (actor != null && actor is AnimatedActor video)
                        {
                            video.Loop = vp.Loop;
                            video.Scaling = vp.Scaling;
                            video.Alignment = vp.Alignment;
                            return video;
                        }
                    }
                    else if (preload is ImageSequenceDef ai)
                    {
                        var video = new ImageSequence(_flexDMD.AssetManager, ai._images);
                        video.FPS = ai.Fps;
                        video.Loop = ai.Loop;
                        video.Scaling = ai.Scaling;
                        video.Alignment = ai.Alignment;
                        return video;
                    }
                }
                else
                {
                    var path = filename.Replace(',', '|');
                    if (path.Contains("|"))
                    {
                        var video = new ImageSequence(_flexDMD.AssetManager, path);
                        return video;
                    }
                    else
                    {
                        var src = _flexDMD.AssetManager.ResolveSrc(path);
                        if (src.AssetType == AssetType.Image)
                        {
                            return new FlexDMD.Image(_flexDMD.AssetManager, path);
                        }
                        else if (src.AssetType == AssetType.Video || src.AssetType == AssetType.Gif || src.AssetType == AssetType.APNG)
                        {
                            var actor = (AnimatedActor)_flexDMD.NewVideo("", path);
                            if (actor != null)
                            {
                                switch (_stretchMode)
                                {
                                    case 0:
                                        actor.Scaling = Scaling.Stretch;
                                        actor.Alignment = Alignment.Center;
                                        break;
                                    case 1:
                                        actor.Scaling = Scaling.FillX;
                                        actor.Alignment = Alignment.Top;
                                        break;
                                    case 2:
                                        actor.Scaling = Scaling.FillX;
                                        actor.Alignment = Alignment.Center;
                                        break;
                                    case 3:
                                        actor.Scaling = Scaling.FillX;
                                        actor.Alignment = Alignment.Bottom;
                                        break;
                                }
                                return actor;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                log.Error(e, "Exception while resolving image: '{0}'", filename);
            }
            return useFrame ? new Frame() : new Actor();
        }

        public void LoadSetup()
        {
            var color = Registry.CurrentUser.OpenSubKey("Software")?.OpenSubKey("UltraDMD")?.GetValue("color");
            if (color != null && color is string c)
            {
                var col = Color.FromName(c);
                if (col.R != 0 || col.G != 0 || col.B != 0) _flexDMD.Color = col;
            }
            var fullcolor = Registry.CurrentUser.OpenSubKey("Software")?.OpenSubKey("UltraDMD")?.GetValue("fullcolor");
            if (fullcolor != null && fullcolor is string fc)
            {
                if ("True".Equals(fc, StringComparison.InvariantCultureIgnoreCase))
                    _flexDMD.RenderMode = RenderMode.DMD_RGB;
                else
                    _flexDMD.RenderMode = RenderMode.DMD_GRAY_4;
            }
        }

        public void Init() => _flexDMD.Run = true;
        public void Uninit() => _flexDMD.Run = false;

        public int GetMajorVersion()
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            return Math.Max(1, fvi.FileMajorPart);
        }

        public int GetMinorVersion()
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            return Math.Max(4, fvi.FileMinorPart);
        }

        public int GetBuildNumber()
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            return fvi.FileBuildPart * 10000 + fvi.FilePrivatePart;
        }

        public bool SetVisibleVirtualDMD(bool bVisible)
        {
            log.Info("SetVisibleVirtualDMD({0})", bVisible);
            bool wasVisible = _visible;
            _visible = bVisible;
            _flexDMD.Show = bVisible;
            return wasVisible;
        }

        public bool SetFlipY(bool flipY)
        {
            log.Error("SetFlipY is not yet supported in FlexDMD");
            return false;
        }

        public bool IsRendering()
        {
            return !_queue.IsFinished();
        }

        public void CancelRendering()
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("CancelRendering");
                _queue.RemoveAllScenes();
            });
        }

        public void CancelRenderingWithId(string sceneId)
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("CancelRenderingWithId");
                _queue.RemoveScene(sceneId);
            });
        }

        public void Clear()
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("Clear");
                _flexDMD.Graphics.Clear(Color.Black);
                _scoreBoard.Visible = false;
                if (_queue.IsFinished()) _queue.Visible = false;
            });
        }

        public void SetProjectFolder(string basePath) => _flexDMD.ProjectFolder = basePath;

        // Stretch: 0, crop to top: 1, crop to center: 2, crop to bottom: 3
        public void SetVideoStretchMode(int mode) => _stretchMode = mode;

        public int CreateAnimationFromImages(int fps, bool loop, string imagelist)
        {
            var id = _nextId;
            _preloads[id] = new ImageSequenceDef(imagelist.Replace(',', '|'), fps, loop);
            _nextId++;
            return id;
        }

        public int RegisterVideo(int videoStretchMode, bool loop, string videoFilename)
        {
            var v = new VideoDef { Loop = loop, VideoFilename = videoFilename };
            switch (videoStretchMode)
            {
                case 0:
                    v.Scaling = Scaling.Stretch;
                    v.Alignment = Alignment.Center;
                    break;
                case 1:
                    v.Scaling = Scaling.FillX;
                    v.Alignment = Alignment.Top;
                    break;
                case 2:
                    v.Scaling = Scaling.FillX;
                    v.Alignment = Alignment.Center;
                    break;
                case 3:
                    v.Scaling = Scaling.FillX;
                    v.Alignment = Alignment.Bottom;
                    break;
            }

            foreach (KeyValuePair<int, object> pair in _preloads)
            {
                if (EqualityComparer<object>.Default.Equals(pair.Value, v))
                {
                    return pair.Key;
                }
            }
            var id = _nextId;
            _preloads[id] = v;
            _nextId++;
            return id;
        }

        private FlexDMD.Font GetFont(string path, float brightness, float outlineBrightness)
        {
            brightness = brightness > 1f ? 1f : brightness;
            outlineBrightness = outlineBrightness > 1f ? 1f : outlineBrightness;
            var baseColor = _flexDMD.RenderMode == RenderMode.DMD_RGB ? _flexDMD.Color : Color.White;
            var tint = brightness >= 0f ? Color.FromArgb((int)(baseColor.R * brightness), (int)(baseColor.G * brightness), (int)(baseColor.B * brightness)) : Color.FromArgb(0, 0, 0, 0);
            if (outlineBrightness >= 0f)
            {
                var borderTint = Color.FromArgb((int)(baseColor.R * outlineBrightness), (int)(baseColor.G * outlineBrightness), (int)(baseColor.B * outlineBrightness));
                return _flexDMD.NewFont(path, tint, borderTint, 1);
            }
            else
            {
                return _flexDMD.NewFont(path, tint, Color.White, 0);
            }
        }

        private Label GetFittedLabel(string text, float fillBrightness, float outlineBrightness)
        {
            foreach (FontDef f in _singleLineFont)
            {
                var font = GetFont(f.Path, fillBrightness, outlineBrightness);
                var label = new Label(_flexDMD, font, text);
                label.SetPosition((_flexDMD.Width - label.Width) / 2, (_flexDMD.Height - label.Height) / 2);
                if ((label.X >= 0 && label.Y >= 0) || f == _singleLineFont[_singleLineFont.Length - 1]) return label;
            }
            return null;
        }

        public void DisplayVersionInfo()
        {
            // No version info in FlexDMD (this is an implementation choice to avoid delaying game startup and displaying again and again the same scene)
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("DisplayVersionInfo");
                _scoreBoard.Visible = false;
                _queue.Visible = false;
            });
        }

        public void DisplayScene00(string background, string toptext, int topBrightness, string bottomtext, int bottomBrightness, int animateIn, int pauseTime, int animateOut)
        {
            DisplayScene00ExWithId("", false, background, toptext, topBrightness, -15, bottomtext, bottomBrightness, -15, animateIn, pauseTime, animateOut);
        }

        public void DisplayScene00Ex(string background, string toptext, int topBrightness, int topOutlineBrightness, string bottomtext, int bottomBrightness, int bottomOutlineBrightness, int animateIn, int pauseTime, int animateOut)
        {
            DisplayScene00ExWithId("", false, background, toptext, topBrightness, topOutlineBrightness, bottomtext, bottomBrightness, bottomOutlineBrightness, animateIn, pauseTime, animateOut);
        }

        public void DisplayScene00ExWithId(string sceneId, bool cancelPrevious, string background, string toptext, int topBrightness, int topOutlineBrightness, string bottomtext, int bottomBrightness, int bottomOutlineBrightness, int animateIn, int pauseTime, int animateOut)
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("DisplayScene00ExWithId '{0}', '{1}', '{2}', '{3}', '{4}', '{5}', '{6}', '{7}', '{8}', '{9}', '{10}', '{11}' {12}", sceneId, cancelPrevious, background, toptext, topBrightness, topOutlineBrightness, bottomtext, bottomBrightness, bottomOutlineBrightness, animateIn, pauseTime, animateOut, DateTimeOffset.Now.ToUnixTimeMilliseconds());
                if (cancelPrevious && sceneId != null && sceneId.Length > 0)
                {
                    var s = _queue.ActiveScene;
                    if (s != null && s.Name == sceneId) _queue.RemoveScene(sceneId);
                }
                _scoreBoard.Visible = false;
                _queue.Visible = true;
                if (toptext != null && toptext.Length > 0 && bottomtext != null && bottomtext.Length > 0)
                {
                    var fontTop = GetFont(_twoLinesFontTop.Path, topBrightness / 15f, topOutlineBrightness / 15f);
                    var fontBottom = GetFont(_twoLinesFontBottom.Path, bottomBrightness / 15f, bottomOutlineBrightness / 15f);
                    var scene = new TwoLineScene(_flexDMD, ResolveImage(background, true), toptext, fontTop, bottomtext, fontBottom, (AnimationType)animateIn, pauseTime / 1000f, (AnimationType)animateOut, sceneId);
                    _queue.Enqueue(scene);
                }
                else if (toptext != null && toptext.Length > 0)
                {
                    var font = GetFittedLabel(toptext, topBrightness / 15f, topOutlineBrightness / 15f).Font;
                    var scene = new SingleLineScene(_flexDMD, ResolveImage(background, true), toptext, font, (AnimationType)animateIn, pauseTime / 1000f, (AnimationType)animateOut, false, sceneId);
                    _queue.Enqueue(scene);
                }
                else if (bottomtext != null && bottomtext.Length > 0)
                {
                    var font = GetFittedLabel(bottomtext, bottomBrightness / 15f, bottomOutlineBrightness / 15f).Font;
                    var scene = new SingleLineScene(_flexDMD, ResolveImage(background, true), bottomtext, font, (AnimationType)animateIn, pauseTime / 1000f, (AnimationType)animateOut, false, sceneId);
                    _queue.Enqueue(scene);
                }
                else
                {
                    var scene = new BackgroundScene(_flexDMD, ResolveImage(background, true), (AnimationType)animateIn, pauseTime / 1000f, (AnimationType)animateOut, sceneId);
                    _queue.Enqueue(scene);
                }
            });
        }

        public void ModifyScene00(string id, string toptext, string bottomtext)
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("ModifyScene00");
                var scene = _queue.ActiveScene;
                if (scene != null && id != null && id.Length > 0 && scene.Name == id)
                {
                    if (scene is TwoLineScene s2) s2.SetText(toptext, bottomtext);
                    if (scene is SingleLineScene s1) s1.SetText(toptext);
                }
            });
        }

        public void ModifyScene00Ex(string id, string toptext, string bottomtext, int pauseTime)
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("ModifyScene00Ex");
                var scene = _queue.ActiveScene;
                if (scene != null && id != null && id.Length > 0 && scene.Name == id)
                {
                    if (scene is TwoLineScene s2) s2.SetText(toptext, bottomtext);
                    if (scene is SingleLineScene s1) s1.SetText(toptext);
                    scene.Pause = scene.Time + pauseTime / 1000f;
                }
            });
        }

        public void DisplayScene01(string sceneId, string background, string text, int textBrightness, int textOutlineBrightness, int animateIn, int pauseTime, int animateOut)
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("DisplayScene01");
                var font = GetFont(_singleLineFont[0].Path, textBrightness / 15f, textOutlineBrightness / 15f);
                var scene = new SingleLineScene(_flexDMD, ResolveImage(background, false), text, font, (AnimationType)animateIn, pauseTime / 1000f, (AnimationType)animateOut, true, sceneId);
                _scoreBoard.Visible = false;
                _queue.Visible = true;
                _queue.Enqueue(scene);
            });
        }

        public void SetScoreboardBackgroundImage(string filename, int selectedBrightness, int unselectedBrightness)
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("SetScoreboardBackgroundImage");
                _scoreBoard.SetBackground(ResolveImage(filename, false));
                _scoreBoard.SetFonts(
                    GetFont(_scoreFontNormal.Path, unselectedBrightness / 15f, -1),
                    GetFont(_scoreFontHighlight.Path, selectedBrightness / 15f, -1),
                    GetFont(_scoreFontText.Path, unselectedBrightness / 15f, -1));
            });
        }

        public void DisplayScoreboard(int cPlayers, int highlightedPlayer, Int64 score1, Int64 score2, Int64 score3, Int64 score4, string lowerLeft, string lowerRight)
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("DisplayScoreboard");
                // Direct rendering: render only if the scene queue is empty, and no direct rendering has happened (managed by scoreboard visibility instead of direct rendering to allow animated scoreboard)
                _scoreBoard.SetNPlayers(cPlayers);
                _scoreBoard.SetHighlightedPlayer(highlightedPlayer);
                _scoreBoard.SetScore(score1, score2, score3, score4);
                _scoreBoard._lowerLeft.Text = lowerLeft;
                _scoreBoard._lowerRight.Text = lowerRight;
                if (_queue.IsFinished())
                {
                    _queue.Visible = false;
                    _scoreBoard.Visible = true;
                }
            });
        }

        // KissDMDv2.vbs use this undocumented function as far as I know. As far as I can tell, this will just change the font.
        // UltraDMD.DisplayScoreboard00 PlayersPlayingGame, 0, Score(1), Score(2), Score(3), Score(4), "credits " & Credits, ""
        public void DisplayScoreboard00(int cPlayers, int highlightedPlayer, Int64 score1, Int64 score2, Int64 score3, Int64 score4, string lowerLeft, string lowerRight)
        {
            // TODO use an UltraDMD matching font
            DisplayScoreboard(cPlayers, highlightedPlayer, score1, score2, score3, score4, lowerLeft, lowerRight);
        }

        public void DisplayText(string text, int textBrightness, int textOutlineBrightness)
        {
            _flexDMD.Post(() =>
            {
                log.Error("DisplayText [untested] '{0}', {1}, {2}", text, textBrightness, textOutlineBrightness);
                _scoreBoard.Visible = false;
                if (_queue.IsFinished())
                {
                    _queue.Visible = false;
                    GetFittedLabel(text, textBrightness / 15f, textOutlineBrightness / 15f).Draw(_flexDMD.Graphics);
                }
            });
        }

        // I did not find an UltraDMD table using this, and I did not succeed in making it work in UltraDMD. So I'm just guessing it's behavior, font, etc.
        public void ScrollingCredits(string background, string text, int textBrightness, int animateIn, int pauseTime, int animateOut)
        {
            _flexDMD.Post(() =>
            {
                if (LOG_DEBUG) log.Debug("ScrollingCredits '{0}', '{1}', '{2}', '{3}', '{4}', '{5}'", background, text, textBrightness, animateIn, pauseTime, animateOut);
                _scoreBoard.Visible = false;
                string[] lines = text.Split(new char[] { '\n', '|' });
                var font12 = GetFont(_scoreFontText.Path, textBrightness / 15f, -1);
                var scene = new ScrollingCreditsScene(_flexDMD, ResolveImage(background, false), lines, font12, (AnimationType)animateIn, pauseTime / 1000f, (AnimationType)animateOut);
                _queue.Visible = true;
                _queue.Enqueue(scene);
            });
        }
    }
}
