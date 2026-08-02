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
 */

using System;

namespace OpenSim.Region.Framework.Scenes
{
    /// <summary>
    /// Multi-octave Perlin noise heightmap generation for new regions, so a freshly
    /// created region gets natural-looking terrain instead of a flat plane or a single
    /// pinhead mound. Two ready-made styles: "Mainland" (terrain out to the region
    /// edges, blended down to water height at the borders) and "Island" (terrain masked
    /// by a radial gradient so the edges sit underwater).
    /// </summary>
    internal static class TerrainPerlin
    {
        private static readonly Random random = new();

        private static float[][] GetEmptyArray(int width, int height)
        {
            float[][] arr = new float[width][];
            for (int i = 0; i < width; i++)
                arr[i] = new float[height];
            return arr;
        }

        private static float Interpolate(float x0, float x1, float alpha)
        {
            return x0 * (1 - alpha) + alpha * x1;
        }

        private static float[][] GenerateWhiteNoise(int width, int height)
        {
            float[][] noise = GetEmptyArray(width, height);
            for (int i = 0; i < width; i++)
                for (int j = 0; j < height; j++)
                    noise[i][j] = (float)random.NextDouble() % 1;
            return noise;
        }

        private static float[][] GenerateSmoothNoise(float[][] baseNoise, int octave)
        {
            int width = baseNoise.Length;
            int height = baseNoise[0].Length;

            float[][] smoothNoise = GetEmptyArray(width, height);

            int samplePeriod = 1 << octave; // 2 ^ octave
            float sampleFrequency = 1.0f / samplePeriod;

            for (int i = 0; i < width; i++)
            {
                int sample_i0 = (i / samplePeriod) * samplePeriod;
                int sample_i1 = (sample_i0 + samplePeriod) % width;
                float horizontal_blend = (i - sample_i0) * sampleFrequency;

                for (int j = 0; j < height; j++)
                {
                    int sample_j0 = (j / samplePeriod) * samplePeriod;
                    int sample_j1 = (sample_j0 + samplePeriod) % height;
                    float vertical_blend = (j - sample_j0) * sampleFrequency;

                    float top = Interpolate(baseNoise[sample_i0][sample_j0], baseNoise[sample_i1][sample_j0], horizontal_blend);
                    float bottom = Interpolate(baseNoise[sample_i0][sample_j1], baseNoise[sample_i1][sample_j1], horizontal_blend);

                    smoothNoise[i][j] = Interpolate(top, bottom, vertical_blend);
                }
            }

            return smoothNoise;
        }

        private static float[][] GeneratePerlinNoise(int width, int height, int octaveCount)
        {
            float[][] baseNoise = GenerateWhiteNoise(width, height);
            float[][] perlinNoise = GetEmptyArray(width, height);

            float persistence = 0.25f;
            float amplitude = 1.0f;
            float totalAmplitude = 0.0f;

            for (int octave = octaveCount - 1; octave >= 0; octave--)
            {
                totalAmplitude += amplitude;
                float[][] smoothNoise = GenerateSmoothNoise(baseNoise, octave);

                for (int i = 0; i < width; i++)
                    for (int j = 0; j < height; j++)
                        perlinNoise[i][j] += smoothNoise[i][j] * amplitude;

                amplitude *= persistence;
            }

            for (int i = 0; i < width; i++)
                for (int j = 0; j < height; j++)
                    perlinNoise[i][j] /= totalAmplitude;

            return perlinNoise;
        }

        private static float[][] AdjustLevels(float[][] image, float low, float high)
        {
            int width = image.Length;
            int height = image[0].Length;
            float[][] result = GetEmptyArray(width, height);

            for (int i = 0; i < width; i++)
            {
                for (int j = 0; j < height; j++)
                {
                    float col = image[i][j];
                    result[i][j] = col <= low ? 0 : col >= high ? 1 : (col - low) / (high - low);
                }
            }

            return result;
        }

        private static float[][] MapToGreyScale(float[][] values)
        {
            int width = values.Length;
            int height = values[0].Length;
            float[][] result = GetEmptyArray(width, height);

            for (int i = 0; i < width; i++)
                for (int j = 0; j < height; j++)
                    result[i][j] = 128 * values[i][j];

            return result;
        }

        private static float[][] SmoothHeightMap(float[][] values)
        {
            int width = values.Length;
            int height = values[0].Length;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float average = 0.0f;
                    int points = 0;

                    if (x - 1 >= 0) { average += values[x - 1][y]; points++; }
                    if (x + 1 < width - 1) { average += values[x + 1][y]; points++; }
                    if (y - 1 >= 0) { average += values[x][y - 1]; points++; }
                    if (y + 1 < height - 1) { average += values[x][y + 1]; points++; }
                    if (x - 1 >= 0 && y - 1 >= 0) { average += values[x - 1][y - 1]; points++; }
                    if (x + 1 < width && y - 1 >= 0) { average += values[x + 1][y - 1]; points++; }
                    if (x - 1 >= 0 && y + 1 < height) { average += values[x - 1][y + 1]; points++; }
                    if (x + 1 < width && y + 1 < height) { average += values[x + 1][y + 1]; points++; }

                    average += values[x][y];
                    points++;

                    values[x][y] = average / points;
                }
            }

            return values;
        }

        private static float[][] Rescale(float[][] values, float min, float max)
        {
            float desiredRange = max - min;

            float currMin = float.MaxValue;
            float currMax = float.MinValue;

            int width = values.Length;
            int height = values[0].Length;

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float h = values[x][y];
                    if (h < currMin) currMin = h;
                    else if (h > currMax) currMax = h;
                }
            }

            float currRange = currMax - currMin;
            float scale = currRange < 0.0001f ? 0f : desiredRange / currRange;

            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    values[x][y] = min + ((values[x][y] - currMin) * scale);

            return values;
        }

        /// <summary>
        /// Blends the edges of a heightmap down to edgeLevel, so terrain that fills the whole
        /// region still meets the region boundary at a consistent (water) height.
        /// </summary>
        private static float[][] EdgeBlendMainlandMap(float[][] map, float edgeLevel)
        {
            int width = map.Length;
            int height = map[0].Length;
            int wVar = (int)(width * 0.13);
            int hVar = (int)(height * 0.11);

            float cx = (width - 1) / 2f;
            float cy = (height - 1) / 2f;

            float[][] blend_map = GetEmptyArray(width, height);

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float rx = cx - Math.Abs(cx - x);
                    float ry = cy - Math.Abs(cy - y);

                    float edgeDistance = rx < ry ? rx : ry;
                    edgeDistance = Math.Abs(rx - ry) < 0.001f ? (float)(1.4 * rx) : edgeDistance;

                    if (rx < 1 || ry < 1)
                        blend_map[x][y] = edgeLevel;
                    else if (rx > wVar + random.Next(-2, 2) && ry > hVar + random.Next(-2, 2))
                        blend_map[x][y] = map[x][y];
                    else
                    {
                        float factor = 2 * edgeDistance / (wVar + edgeDistance);
                        blend_map[x][y] = edgeLevel + (map[x][y] - edgeLevel) * factor;
                    }
                }
            }

            return blend_map;
        }

        /// <summary>
        /// Generates a radial gradient mask (1 in the middle, fading to ~0 at the edges) so a
        /// heightmap multiplied by it reads as an island surrounded by water.
        /// </summary>
        private static float[][] GenerateIslandGradientMap(int width, int height)
        {
            int wVar = (int)(width * 0.13);
            int hVar = (int)(height * 0.11);

            float cx = width / 2f + random.Next(-1 * wVar, wVar);
            float cy = height / 2f + random.Next(-1 * hVar, hVar);
            float minRad = (float)Math.Sqrt(cx * cx + cy * cy) * 0.55f;
            float maxRad = minRad * 1.33f;

            float[][] gradient_map = GetEmptyArray(width, height);

            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float rx = cx - x;
                    float ry = cy - y;
                    float rad = (float)Math.Sqrt(rx * rx + ry * ry);
                    float tRad = minRad * (float)(0.47 + random.NextDouble() / 1.3);

                    float edgeDistance = maxRad - rad;
                    if (edgeDistance >= tRad)
                        gradient_map[x][y] = 1;
                    else if (edgeDistance <= 1)
                        gradient_map[x][y] = 0.001f;
                    else
                    {
                        float factor = minRad / (random.Next(3, 9) * edgeDistance + minRad);
                        gradient_map[x][y] = 1 - factor;
                    }
                }
            }

            return gradient_map;
        }

        /// <summary>
        /// Terrain out to the region edges, blended down to <paramref name="min"/> at the borders.
        /// </summary>
        public static float[][] GenerateMainlandTerrain(int width, int height, float min, float max, int octaveCount = 8, int smoothing = 2)
        {
            if (width <= 0) width = 256;
            if (height <= 0) height = 256;
            if (octaveCount <= 0) octaveCount = 8;

            float[][] perlinMap = MapToGreyScale(AdjustLevels(GeneratePerlinNoise(width, height, octaveCount), 0.2f, 0.8f));

            for (int i = 0; i < smoothing; i++)
                perlinMap = SmoothHeightMap(perlinMap);

            perlinMap = Rescale(perlinMap, min, max);
            perlinMap = EdgeBlendMainlandMap(perlinMap, min);

            return perlinMap;
        }

        /// <summary>
        /// Terrain masked by a radial gradient so the region edges sit underwater.
        /// </summary>
        public static float[][] GenerateIslandTerrain(int width, int height, float min, float max, int octaveCount = 8, int smoothing = 2)
        {
            if (width <= 0) width = 256;
            if (height <= 0) height = 256;
            if (octaveCount <= 0) octaveCount = 8;

            float[][] perlinMap = MapToGreyScale(AdjustLevels(GeneratePerlinNoise(width, height, octaveCount), 0.2f, 0.8f));

            float[][] mask = GenerateIslandGradientMap(width, height);
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    perlinMap[x][y] *= mask[x][y];

            for (int i = 0; i < smoothing; i++)
                perlinMap = SmoothHeightMap(perlinMap);

            return Rescale(perlinMap, min, max);
        }
    }
}
