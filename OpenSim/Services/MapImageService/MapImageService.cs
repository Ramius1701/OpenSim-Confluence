/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 *
 * The design of this map service is based on SimianGrid's PHP-based
 * map service. See this URL for the original PHP version:
 * https://github.com/openmetaversefoundation/simiangrid/
 */

using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Base;
using OpenSim.Services.Interfaces;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;


namespace OpenSim.Services.MapImageService
{
    public class MapImageService : ServiceBase, IMapImageService
    {
        private static readonly ILog m_log = LogManager.GetLogger( MethodBase.GetCurrentMethod().DeclaringType);
#pragma warning disable 414
        private string LogHeader = "[MAP IMAGE SERVICE]";
#pragma warning restore 414

        private const int ZOOM_LEVELS = 8;
        private const int IMAGE_WIDTH = 256;
        private const int HALF_WIDTH = 128;
        private const int JPEG_QUALITY = 80;

        private static string m_TilesStoragePath = "maptiles";

        private static object m_Sync = new object();
        private static bool m_Initialized = false;
        private static Color m_Watercolor = Color.FromArgb(29, 72, 96);
        private static Bitmap m_WaterBitmap = null;
        private static byte[] m_WaterJPEGBytes = null;

        public MapImageService(IConfigSource config) : base(config)
        {
            lock (m_Sync)
            {
                if (!m_Initialized)
                {
                    m_Initialized = true;
                    m_log.Debug("[MAP IMAGE SERVICE]: Starting MapImage service");

                    IConfig serviceConfig = config.Configs["MapImageService"];
                    if (serviceConfig is not null)
                    {
                        m_TilesStoragePath = serviceConfig.GetString("TilesStoragePath", m_TilesStoragePath);

                        // Opt-in, defaults false - tiles only ever get
                        // refreshed by a region actually uploading a new
                        // one, so on a grid where Robust restarts more
                        // often than its regions do (the normal case on a
                        // multi-machine deployment, and also just true
                        // during active development - found live, this
                        // was hardcoded true in Robust.HG.ini and silently
                        // wiping every tile on every one of many same-day
                        // restarts), wiping this unconditionally on every
                        // Robust startup would leave the map showing
                        // nothing but water tiles until every region
                        // eventually re-uploads - a real regression for
                        // anyone who didn't ask for it. Real use here: a
                        // one-time cleanup after a stale tile from a
                        // region that's since been rebuilt/moved, not a
                        // standing default.
                        //
                        // Checked via the same live IGridSettingsService
                        // store the admin Grid Settings page (Map Tiles
                        // section) already uses for Banker Avatar and
                        // everything else there - an admin can flip this
                        // without touching the ini or restarting Robust
                        // twice (once to change the setting, again to have
                        // it take effect - the setting only matters at
                        // this one startup-time check, so a restart is
                        // still needed to actually clear the tiles, but at
                        // least changing the toggle itself doesn't need
                        // filesystem access). Falls back to the ini's own
                        // ClearTilesOnStartup only when the grid setting
                        // has never been touched, so an existing
                        // deployment's current ini value keeps working
                        // exactly as before until an admin explicitly
                        // overrides it from the WebUI.
                        bool clearTiles = serviceConfig.GetBoolean("ClearTilesOnStartup", false);

                        IConfig gridSettingsConfig = config.Configs["GridSettingsService"];
                        string gridSettingsDll = gridSettingsConfig?.GetString("LocalServiceModule", string.Empty);
                        if (!string.IsNullOrEmpty(gridSettingsDll))
                        {
                            try
                            {
                                IGridSettingsService gridSettingsService = LoadPlugin<IGridSettingsService>(gridSettingsDll, new object[] { config });
                                string overrideValue = gridSettingsService?.Get("ClearMapTilesOnStartup");
                                if (!string.IsNullOrEmpty(overrideValue))
                                    clearTiles = overrideValue == "true";
                            }
                            catch (Exception e)
                            {
                                m_log.Warn("[MAP IMAGE SERVICE]: Could not load IGridSettingsService - falling back to the ini's own ClearTilesOnStartup value", e);
                            }
                        }

                        if (clearTiles)
                            ClearAllTiles();

                        //memory cache JPEG tile with just water.
                        m_WaterBitmap = new Bitmap(IMAGE_WIDTH, IMAGE_WIDTH, PixelFormat.Format24bppRgb);
                        FillImage(m_WaterBitmap, m_Watercolor);
                        using (MemoryStream ms = new MemoryStream())
                        {
                            m_WaterBitmap.Save(ms, ImageFormat.Jpeg);
                            ms.Seek(0, SeekOrigin.Begin);
                            m_WaterJPEGBytes = ms.ToArray();
                        }
                    }
                }
            }
        }

        private void ClearAllTiles()
        {
            try
            {
                if (Directory.Exists(m_TilesStoragePath))
                {
                    Directory.Delete(m_TilesStoragePath, true);
                    m_log.Info($"[MAP IMAGE SERVICE]: ClearTilesOnStartup=true - deleted cached tiles under {m_TilesStoragePath}");
                }
            }
            catch (Exception e)
            {
                m_log.Warn($"[MAP IMAGE SERVICE]: ClearTilesOnStartup could not clear {m_TilesStoragePath}: {e.Message}");
            }
        }

        #region IMapImageService

        public bool AddMapTile(int x, int y, byte[] imageData, UUID scopeID, out string reason)
        {
            reason = string.Empty;
            ReadOnlySpan<char> path = GetFolder(scopeID);
            string fileName = GetFileName(1, x, y, path);
            lock (m_Sync)
            {
                try
                {
                     File.WriteAllBytes(fileName, imageData);
                }
                catch (Exception e)
                {
                    m_log.WarnFormat("[MAP IMAGE SERVICE]: Unable to save image file {0}: {1}", fileName, e);
                    reason = e.Message;
                    return false;
                }
            }

            return UpdateMultiResolutionFiles(x, y, scopeID);
        }

        public bool RemoveMapTile(int x, int y, UUID scopeID, out string reason)
        {
            reason = string.Empty;
            string fileName = GetFileName(1, x, y, scopeID);

            lock (m_Sync)
            {
                try
                {
                    File.Delete(fileName);
                }
                catch (Exception e)
                {
                    m_log.Warn($"[MAP IMAGE SERVICE]: Unable to save delete file {fileName}: {e.Message}");
                    reason = e.Message;
                    return false;
                }
            }
            return UpdateMultiResolutionFiles(x, y, scopeID);
        }

        // When large varregions start up, they can send piles of new map tiles. This causes
        //    this multi-resolution routine to be called a zillion times an causes much CPU
        //    time to be spent creating multi-resolution tiles that will be replaced when
        //    the next maptile arrives.
        private struct MapToMultiRez
        {
            public int x;
            public int y;
            public UUID scopeID;
        }

        private readonly Queue<MapToMultiRez> m_MultiRezToBuild = new Queue<MapToMultiRez>();

        private bool UpdateMultiResolutionFiles(int x, int y, UUID scopeID)
        {
            lock (m_MultiRezToBuild)
            {
                // m_log.DebugFormat("{0} UpdateMultiResolutionFilesAsync: scheduling update for <{1},{2}>", LogHeader, x, y);
                m_MultiRezToBuild.Enqueue(
                    new MapToMultiRez
                    {
                        x = x,
                        y = y,
                        scopeID = scopeID
                    }
                );

                if (m_MultiRezToBuild.Count == 1)
                    Util.FireAndForget(DoUpdateMultiResolutionFilesAsync);
            }

            return true;
        }

        private void DoUpdateMultiResolutionFilesAsync(object o)
        {
            // let acumulate large region tiles
            Thread.Sleep(60 * 1000); // large regions take time to upload tiles

            while (true)
            {
                MapToMultiRez toMultiRez;
                lock (m_MultiRezToBuild)
                {
                    if(!m_MultiRezToBuild.TryDequeue(out toMultiRez))
                        return;
                }

                ReadOnlySpan<char> path = GetFolder(toMultiRez.scopeID);
                for (int zoomLevel = 2; zoomLevel <= ZOOM_LEVELS; zoomLevel++)
                {
                    if (!CreateTile(zoomLevel, toMultiRez.x, toMultiRez.y, path))
                    {
                        m_log.WarnFormat("[MAP IMAGE SERVICE]: Unable to create tile for {0},{1} at zoom level {1}", toMultiRez.x, toMultiRez.y, zoomLevel);
                        return;
                    }
                }
                Thread.Sleep(50); // slow things a bit
            }
        }

        public byte[] GetMapTile(string fileName, UUID scopeID, out string format)
        {
            //m_log.DebugFormat("[MAP IMAGE SERVICE]: Getting map tile {0}", fileName);
            string fullName = Path.Combine(m_TilesStoragePath, scopeID.ToString());
            fullName = Path.Combine(fullName, fileName);
            try
            {
                lock (m_Sync)
                { 
                    format = Path.GetExtension(fileName).ToLower();
                    //m_log.DebugFormat("[MAP IMAGE SERVICE]: Found file {0}, extension {1}", fileName, format);
                    return File.ReadAllBytes(fullName);
                }
            }

            catch
            {
                format = ".jpg";
                return m_WaterJPEGBytes is null ? Array.Empty<byte>() : m_WaterJPEGBytes;
            }
        }
        #endregion

        private string GetFileName(int zoomLevel, int x, int y, UUID scopeID)
        {
            string path = Path.Combine(m_TilesStoragePath, scopeID.ToString());
            return Path.Combine(path, string.Format("map-{0}-{1}-{2}-objects.{3}", zoomLevel, x, y, "jpg"));
        }
        private string GetFileName(int zoomLevel, int x, int y, ReadOnlySpan<char> path)
        {
            return Path.Combine(path.ToString(), string.Format("map-{0}-{1}-{2}-objects.{3}", zoomLevel, x, y, "jpg"));
        }

        private string GetFolder(UUID scopeID)
        {
            string path = Path.Combine(m_TilesStoragePath, scopeID.ToString());
            Directory.CreateDirectory(path);
            return path;
        }

        private Bitmap GetInputTileImage(string fileName)
        {
            try
            {
                lock(m_Sync)
                {
                    if (File.Exists(fileName))
                    {
                        Bitmap bm = new Bitmap(fileName);
                        if (bm.Width != IMAGE_WIDTH || bm.Height != IMAGE_WIDTH || bm.PixelFormat != PixelFormat.Format24bppRgb)
                        {
                            m_log.Error($"[MAP IMAGE SERVICE]: invalid map tile {fileName}: {bm.Width} , {bm.Height}, {bm.PixelFormat}");
                            bm.Dispose();
                            return null;
                        }
                        return bm;
                    }
                }
            }
            catch (Exception e)
            {
                m_log.Warn($"[MAP IMAGE SERVICE]: Unable to read image data from {fileName}: {e.Message}");
            }

            return null;
        }

        private Bitmap GetOutputTileImage(string fileName)
        {
            try
            {
                lock(m_Sync)
                {
                    return File.Exists(fileName) ? new Bitmap(fileName) : new Bitmap(IMAGE_WIDTH, IMAGE_WIDTH, PixelFormat.Format24bppRgb);
                }
            }
            catch (Exception e)
            {
                m_log.WarnFormat("[MAP IMAGE SERVICE]: Unable to read image data from {0}: {1}", fileName, e);
            }

            return null;
        }

        private bool CreateTile(int zoomLevel, int inx, int iny, ReadOnlySpan<char> path)
        {
            int previusLevel = zoomLevel - 1;
            int prevStep = 1 << previusLevel - 1;

            int mask = unchecked((int)0xffffffff) << previusLevel;

            // Convert x and y to the bottom left of current tile
            int x = inx & mask;
            int y = iny & mask;

            int ntiles = 0;
            Bitmap output = (Bitmap)m_WaterBitmap.Clone();

            Bitmap input = GetInputTileImage(GetFileName(previusLevel, x, y, path));
            if (input is not null)
            {
                ImageCopyResampled(output, input, 0, HALF_WIDTH);
                input.Dispose();
                ntiles++;
            }
            input = GetInputTileImage(GetFileName(previusLevel, x + prevStep, y, path));
            if (input is not null)
            {
                ImageCopyResampled(output, input, HALF_WIDTH, HALF_WIDTH);
                input.Dispose();
                ntiles++;
            }
            input = GetInputTileImage(GetFileName(previusLevel, x, y + prevStep, path));
            if (input is not null)
            {
                ImageCopyResampled(output, input, 0, 0);
                input.Dispose();
                ntiles++;
            }
            input = GetInputTileImage(GetFileName(previusLevel, x + prevStep, y + prevStep, path));
            if (input is not null)
            {
                ImageCopyResampled(output, input, HALF_WIDTH, 0);
                input.Dispose();
                ntiles++;
            }

            string outputFile = GetFileName(zoomLevel, x, y, path);
            try
            {
                lock(m_Sync)
                {
                    File.Delete(outputFile);
                    if (ntiles > 0)
                        output.Save(outputFile, ImageFormat.Jpeg);
                }
            }
            catch (Exception e)
            {
                m_log.Warn($"[MAP IMAGE SERVICE]: Oops on saving {outputFile} {e.Message}");
            }

            output.Dispose();
            return true;
        }

        #region Image utilities

        private void FillImage(Bitmap bm, Color c)
        {
            BitmapData srcData = bm.LockBits(new Rectangle(0, 0, bm.Width, bm.Height), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            byte r = c.R;
            byte g = c.G;
            byte b = c.B;
            unsafe
            {
                byte* ptr = (byte*)srcData.Scan0;
                for(int y = 0; y < bm.Height; y++)
                {
                    for(int x = 0; x < bm.Width; x++)
                    {
                        *ptr++ = b;
                        *ptr++ = g;
                        *ptr++ = r;
                    }
                }
            }
            bm.UnlockBits(srcData);
         }

        private void ImageCopyResampled(Bitmap output, Bitmap input, int destX, int destY)
        {
            try
            {
                BitmapData srcData = input.LockBits(new Rectangle(0, 0, input.Width, input.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                BitmapData dstData = output.LockBits(new Rectangle(0, 0, output.Width, output.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
                unsafe
                {
                    byte* srcPointer = (byte*)srcData.Scan0;
                    byte* srcPointer2 = (byte*)srcData.Scan0 + srcData.Stride;
                    byte* dstPointer = (byte*)dstData.Scan0 + destY * dstData.Stride + 3 * destX;
                    for (int y = 0; y < HALF_WIDTH; y++)
                    {
                        byte* dxptr = dstPointer;
                        for (int i = 0; i < HALF_WIDTH; i++)
                        {
                            // Blue
                            int t = srcPointer[0] + srcPointer[3] + srcPointer2[0] + srcPointer2[3];
                            dxptr[0] = (byte)(t >> 2);
                            // Green
                            t = srcPointer[1] + srcPointer[4] + srcPointer2[1] + srcPointer2[4];
                            dxptr[1] = (byte)(t >> 2);

                            // Red
                            t = srcPointer[2] + srcPointer[5] + srcPointer2[2] + srcPointer2[5];
                            dxptr[2] = (byte)(t >> 2);

                            /*
                            dxptr[0] = srcPointer[0]; // Blue
                            dxptr[1] = srcPointer[1]; // Green
                            dxptr[2] = srcPointer[2]; // Red
                            */
                            srcPointer += 6; // skip one point
                            srcPointer2 += 6; // skip one point
                            dxptr += 3;
                        }
                        srcPointer += srcData.Stride; // skip extra line
                        srcPointer2 += srcData.Stride;
                        dstPointer += dstData.Stride;
                    }
                }

                output.UnlockBits(dstData);
                input.UnlockBits(srcData);
            }
            //catch (InvalidOperationException e)
            catch
            {
            }
        }

        #endregion
    }
}
