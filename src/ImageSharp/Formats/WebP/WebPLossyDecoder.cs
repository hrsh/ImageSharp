// Copyright (c) Six Labors and contributors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;

using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.Memory;

namespace SixLabors.ImageSharp.Formats.WebP
{
    internal sealed class WebPLossyDecoder : WebPDecoderBase
    {
        private readonly Vp8BitReader bitReader;

        private readonly MemoryAllocator memoryAllocator;

        public WebPLossyDecoder(Vp8BitReader bitReader, MemoryAllocator memoryAllocator)
        {
            this.memoryAllocator = memoryAllocator;
            this.bitReader = bitReader;
        }

        public void Decode<TPixel>(Buffer2D<TPixel> pixels, int width, int height, WebPImageInfo info)
            where TPixel : struct, IPixel<TPixel>
        {
            // we need buffers for Y U and V in size of the image
            // TODO: increase size to enable using all prediction blocks? (see https://tools.ietf.org/html/rfc6386#page-9 )
            Buffer2D<YUVPixel> yuvBufferCurrentFrame = this.memoryAllocator.Allocate2D<YUVPixel>(width, height);

            // TODO: var predictionBuffer - macro-block-sized with approximation of the portion of the image being reconstructed.
            //  those prediction values are the base, the values from DCT processing are added to that

            // TODO residue signal from DCT: 4x4 blocks of DCT transforms, 16Y, 4U, 4V
            Vp8Profile vp8Profile = this.DecodeProfile(info.Vp8Profile);

            // Paragraph 9.2: color space and clamp type follow.
            sbyte colorSpace = (sbyte)this.bitReader.ReadValue(1);
            sbyte clampType = (sbyte)this.bitReader.ReadValue(1);
            var vp8PictureHeader = new Vp8PictureHeader()
                                   {
                                       Width = (uint)width,
                                       Height = (uint)height,
                                       XScale = info.XScale,
                                       YScale = info.YScale,
                                       ColorSpace = colorSpace,
                                       ClampType = clampType
                                   };

            // Paragraph 9.3: Parse the segment header.
            var proba = new Vp8Proba();
            Vp8SegmentHeader vp8SegmentHeader = this.ParseSegmentHeader(proba);

            // Paragraph 9.4: Parse the filter specs.
            Vp8FilterHeader vp8FilterHeader = this.ParseFilterHeader();

            var vp8Io = default(Vp8Io);
            var decoder = new Vp8Decoder(info.Vp8FrameHeader, vp8PictureHeader, vp8FilterHeader, vp8SegmentHeader, proba, vp8Io);

            // Paragraph 9.5: Parse partitions.
            this.ParsePartitions(decoder);

            // Paragraph 9.6: Dequantization Indices.
            this.ParseDequantizationIndices(decoder.SegmentHeader);

            // Ignore the value of update_proba
            this.bitReader.ReadBool();

            // Paragraph 13.4: Parse probabilities.
            this.ParseProbabilities(decoder, decoder.Probabilities);

            this.ParseFrame(decoder, vp8Io);
        }

        private void ParseFrame(Vp8Decoder dec, Vp8Io io)
        {
            for (dec.MbY = 0; dec.MbY < dec.BottomRightMbY; ++dec.MbY)
            {
                // Parse bitstream for this row.
                long bitreaderIdx = dec.MbY & dec.NumPartsMinusOne;
                Vp8BitReader bitreader = dec.Vp8BitReaders[bitreaderIdx];

                // Parse intra mode mode row.
                for (int mbX = 0; mbX < dec.MbWidth; ++mbX)
                {
                    this.ParseIntraMode(dec, mbX);
                }

                for (; dec.MbX < dec.MbWidth; ++dec.MbX)
                {
                    this.DecodeMacroBlock(dec, bitreader);
                }

                // Prepare for next scanline.
                this.InitScanline(dec);

                // Reconstruct, filter and emit the row.
                this.ProcessRow(dec);
            }
        }

        private void ParseIntraMode(Vp8Decoder dec, int mbX)
        {
            Vp8MacroBlockData block = dec.MacroBlockData[mbX];
            byte[] left = dec.IntraL;
            byte[] top = dec.IntraT;

            if (dec.SegmentHeader.UpdateMap)
            {
                // Hardcoded tree parsing.
                block.Segment = this.bitReader.GetBit((int)dec.Probabilities.Segments[0]) != 0
                                    ? (byte)this.bitReader.GetBit((int)dec.Probabilities.Segments[1])
                                    : (byte)this.bitReader.GetBit((int)dec.Probabilities.Segments[2]);
            }
            else
            {
                // default for intra
                block.Segment = 0;
            }

            if (dec.UseSkipProbability)
            {
                block.Skip = (byte)this.bitReader.GetBit(dec.SkipProbability);
            }

            block.IsI4x4 = this.bitReader.GetBit(145) is 0;
            if (!block.IsI4x4)
            {
                // Hardcoded 16x16 intra-mode decision tree.
                int yMode = this.bitReader.GetBit(156) > 0 ?
                                this.bitReader.GetBit(128) > 0 ? WebPConstants.TmPred : WebPConstants.HPred :
                                this.bitReader.GetBit(163) > 0 ? WebPConstants.VPred : WebPConstants.DcPred;
                block.Modes[0] = (byte)yMode;
                for (int i = 0; i < left.Length; i++)
                {
                    left[i] = (byte)yMode;
                    top[i] = (byte)yMode;
                }
            }
            else
            {
                Span<byte> modes = block.Modes.AsSpan();
                for (int y = 0; y < 4; ++y)
                {
                    int yMode = left[y];
                    for (int x = 0; x < 4; ++x)
                    {
                        byte[] prob = WebPConstants.BModesProba[top[x], yMode];
                        int i = WebPConstants.YModesIntra4[this.bitReader.GetBit(prob[0])];
                        while (i > 0)
                        {
                            i = WebPConstants.YModesIntra4[(2 * i) + this.bitReader.GetBit(prob[i])];
                        }

                        yMode = -i;
                        top[x] = (byte)yMode;
                    }

                    top.CopyTo(modes);
                    modes = modes.Slice(4);
                    left[y] = (byte)yMode;
                }
            }

            // Hardcoded UVMode decision tree.
            block.UvMode = (byte)(this.bitReader.GetBit(142) is 0 ? 0 :
                           this.bitReader.GetBit(114) is 0 ? 2 :
                           this.bitReader.GetBit(183) > 0 ? 1 : 3);
        }

        private void InitScanline(Vp8Decoder dec)
        {
            Vp8MacroBlock left = dec.MacroBlockInfo[0];
            left.NoneZeroAcDcCoeffs = 0;
            left.NoneZeroDcCoeffs = 0;
            for (int i = 0; i < dec.IntraL.Length; i++)
            {
                dec.IntraL[i] = 0;
            }

            dec.MbX = 0;
        }

        private void ProcessRow(Vp8Decoder dec)
        {
            bool filterRow = (dec.Filter != LoopFilter.None) &&
                             (dec.MbY >= dec.TopLeftMbY) && (dec.MbY <= dec.BottomRightMbY);

            this.ReconstructRow(dec, filterRow);
        }

        private void ReconstructRow(Vp8Decoder dec, bool filterRow)
        {
            int mby = dec.MbY;

            int yOff = (WebPConstants.Bps * 1) + 8;
            int uOff = yOff + (WebPConstants.Bps * 16) + WebPConstants.Bps;
            int vOff = uOff + 16;

            Span<byte> yDst = dec.YuvBuffer.AsSpan(yOff);
            Span<byte> uDst = dec.YuvBuffer.AsSpan(uOff);
            Span<byte> vDst = dec.YuvBuffer.AsSpan(vOff);

            // Initialize left-most block.
            for (int i = 0; i < 16; ++i)
            {
                yDst[(i * WebPConstants.Bps) - 1] = 129;
            }

            for (int i = 0; i < 8; ++i)
            {
                uDst[(i * WebPConstants.Bps) - 1] = 129;
                vDst[(i * WebPConstants.Bps) - 1] = 129;
            }

            // Init top-left sample on left column too.
            if (mby > 0)
            {
                yDst[-1 - WebPConstants.Bps] = uDst[-1 - WebPConstants.Bps] = vDst[-1 - WebPConstants.Bps] = 129;
            }
            else
            {
                // We only need to do this init once at block (0,0).
                // Afterward, it remains valid for the whole topmost row.
                Span<byte> tmp = dec.YuvBuffer.AsSpan(yOff - WebPConstants.Bps - 1, 16 + 4 + 1);
                for (int i = 0; i < tmp.Length; ++i)
                {
                    tmp[i] = 127;
                }

                tmp = dec.YuvBuffer.AsSpan(uOff - WebPConstants.Bps - 1, 8 + 1);
                for (int i = 0; i < tmp.Length; ++i)
                {
                    tmp[i] = 127;
                }

                tmp = dec.YuvBuffer.AsSpan(vOff - WebPConstants.Bps - 1, 8 + 1);
                for (int i = 0; i < tmp.Length; ++i)
                {
                    tmp[i] = 127;
                }
            }

            // Reconstruct one row.
            for (int mbx = 0; mbx < dec.MbWidth; ++mbx)
            {
                Vp8MacroBlockData block = dec.MacroBlockData[mbx];

                // Rotate in the left samples from previously decoded block. We move four
                // pixels at a time for alignment reason, and because of in-loop filter.
                if (mbx > 0)
                {
                    for (int i = -1; i < 16; ++i)
                    {
                        // Copy32b(&y_dst[j * BPS - 4], &y_dst[j * BPS + 12]);
                    }

                    for (int i = -1; i < 8; ++i)
                    {
                        // Copy32b(&u_dst[j * BPS - 4], &u_dst[j * BPS + 4]);
                        // Copy32b(&v_dst[j * BPS - 4], &v_dst[j * BPS + 4]);
                    }

                    // Bring top samples into the cache.
                    Vp8TopSamples topSamples = dec.YuvTopSamples[mbx];
                    short[] coeffs = block.Coeffs;
                    uint bits = block.NonZeroY;
                    if (mby > 0)
                    {
                        //memcpy(y_dst - BPS, top_yuv[0].y, 16);
                        //memcpy(u_dst - BPS, top_yuv[0].u, 8);
                        //memcpy(v_dst - BPS, top_yuv[0].v, 8);
                    }

                    // Predict and add residuals.
                    if (block.IsI4x4)
                    {
                        if (mby > 0)
                        {
                            if (mbx >= dec.MbWidth - 1)
                            {
                                // On rightmost border.
                                //memset(top_right, top_yuv[0].y[15], sizeof(*top_right));
                            }
                            else
                            {
                                // memcpy(top_right, top_yuv[1].y, sizeof(*top_right));
                            }
                        }

                        // Replicate the top-right pixels below.


                        // Predict and add residuals for all 4x4 blocks in turn.
                        for (int n = 0; n < 16; ++n, bits <<= 2)
                        {

                        }
                    }
                    else
                    {
                        // 16x16

                    }
                }
            }
        }

        private Vp8Profile DecodeProfile(int version)
        {
            switch (version)
            {
                case 0:
                    return new Vp8Profile { ReconstructionFilter = ReconstructionFilter.Bicubic, LoopFilter = LoopFilter.Complex };
                case 1:
                    return new Vp8Profile { ReconstructionFilter = ReconstructionFilter.Bilinear, LoopFilter = LoopFilter.Simple };
                case 2:
                    return new Vp8Profile { ReconstructionFilter = ReconstructionFilter.Bilinear, LoopFilter = LoopFilter.None };
                case 3:
                    return new Vp8Profile { ReconstructionFilter = ReconstructionFilter.None, LoopFilter = LoopFilter.None };
                default:
                    // Reserved for future use in Spec.
                    // https://tools.ietf.org/html/rfc6386#page-30
                    WebPThrowHelper.ThrowNotSupportedException($"unsupported VP8 version {version} found");
                    return new Vp8Profile();
            }
        }

        private void DecodeMacroBlock(Vp8Decoder dec, Vp8BitReader bitreader)
        {
            Vp8MacroBlock left = dec.MacroBlockInfo[0];
            Vp8MacroBlock macroBlock = dec.MacroBlockInfo[1 + dec.MbX];
            Vp8MacroBlockData blockData = dec.MacroBlockData[dec.MbX];
            int skip = dec.UseSkipProbability ? blockData.Skip : 0;

            if (skip is 0)
            {
                this.ParseResiduals(dec, bitreader, macroBlock);
            }
            else
            {
                left.NoneZeroAcDcCoeffs = macroBlock.NoneZeroAcDcCoeffs = 0;
                if (blockData.IsI4x4)
                {
                    left.NoneZeroDcCoeffs = macroBlock.NoneZeroDcCoeffs = 0;
                }

                blockData.NonZeroY = 0;
                blockData.NonZeroUv = 0;
                blockData.Dither = 0;
            }

            // Store filter info.
            if (dec.Filter != LoopFilter.None)
            {
                dec.FilterInfo[dec.MbX] = dec.FilterStrength[blockData.Segment, blockData.IsI4x4 ? 1 : 0];
                dec.FilterInfo[dec.MbX].InnerFiltering |= (byte)(skip is 0 ? 1 : 0);
            }
        }

        private bool ParseResiduals(Vp8Decoder dec, Vp8BitReader br, Vp8MacroBlock mb)
        {
            uint nonZeroY = 0;
            uint nonZeroUv = 0;
            int first;
            var dst = new short[384];
            int dstOffset = 0;
            Vp8MacroBlockData block = dec.MacroBlockData[dec.MbX];
            Vp8QuantMatrix q = dec.DeQuantMatrices[block.Segment];
            Vp8BandProbas[,] bands = dec.Probabilities.BandsPtr;
            Vp8BandProbas[] acProba;
            Vp8MacroBlock leftMb = dec.MacroBlockInfo[0];

            if (!block.IsI4x4)
            {
                // Parse DC
                var dc = new short[16];
                int ctx = (int)(mb.NoneZeroDcCoeffs + leftMb.NoneZeroDcCoeffs);
                int nz = this.GetCoeffs(br, GetBandsRow(bands, 1), ctx, q.Y2Mat, 0, dc);
                mb.NoneZeroDcCoeffs = leftMb.NoneZeroDcCoeffs = (uint)(nz > 0 ? 1 : 0);
                if (nz > 0)
                {
                    // More than just the DC -> perform the full transform.
                    this.TransformWht(dc, dst);
                }
                else
                {
                    int dc0 = (dc[0] + 3) >> 3;
                    for (int i = 0; i < 16 * 16; i += 16)
                    {
                        dst[i] = (short)dc0;
                    }
                }

                first = 1;
                acProba = GetBandsRow(bands, 1);
            }
            else
            {
                first = 0;
                acProba = GetBandsRow(bands, 3);
            }

            byte tnz = (byte)(mb.NoneZeroAcDcCoeffs & 0x0f);
            byte lnz = (byte)(leftMb.NoneZeroAcDcCoeffs & 0x0f);

            for (int y = 0; y < 4; ++y)
            {
                int l = lnz & 1;
                uint nzCoeffs = 0;
                for (int x = 0; x < 4; ++x)
                {
                    int ctx = l + (tnz & 1);
                    int nz = this.GetCoeffs(br, acProba, ctx, q.Y1Mat, first, dst.AsSpan(dstOffset));
                    l = (nz > first) ? 1 : 0;
                    tnz = (byte)((tnz >> 1) | (l << 7));
                    nzCoeffs = NzCodeBits(nzCoeffs, nz, dst[0] != 0 ? 1 : 0);
                    dstOffset += 16;
                }

                tnz >>= 4;
                lnz = (byte)((lnz >> 1) | (l << 7));
                nonZeroY = (nonZeroY << 8) | nzCoeffs;
            }

            uint outTnz = tnz;
            uint outLnz = (uint)(lnz >> 4);

            for (int ch = 0; ch < 4; ch += 2)
            {
                uint nzCoeffs = 0;
                tnz = (byte)(mb.NoneZeroAcDcCoeffs >> (4 + ch));
                lnz = (byte)(leftMb.NoneZeroAcDcCoeffs >> (4 + ch));
                for (int y = 0; y < 2; ++y)
                {
                    int l = lnz & 1;
                    for (int x = 0; x < 2; ++x)
                    {
                        int ctx = l + (tnz & 1);
                        int nz = this.GetCoeffs(br, GetBandsRow(bands, 2), ctx, q.UvMat, 0, dst.AsSpan(dstOffset));
                        l = (nz > 0) ? 1 : 0;
                        tnz = (byte)((tnz >> 1) | (l << 3));
                        nzCoeffs = NzCodeBits(nzCoeffs, nz, dst[0] != 0 ? 1 : 0);
                        dstOffset += 16;
                    }

                    tnz >>= 2;
                    lnz = (byte)((lnz >> 1) | (l << 5));
                }

                // Note: we don't really need the per-4x4 details for U/V blocks.
                nonZeroUv |= nzCoeffs << (4 * ch);
                outTnz |= (uint)((tnz << 4) << ch);
                outLnz |= (uint)((lnz & 0xf0) << ch);
            }

            mb.NoneZeroAcDcCoeffs = outTnz;
            leftMb.NoneZeroAcDcCoeffs = outLnz;

            block.NonZeroY = nonZeroY;
            block.NonZeroUv = nonZeroUv;

            // We look at the mode-code of each block and check if some blocks have less
            // than three non-zero coeffs (code < 2). This is to avoid dithering flat and
            // empty blocks.
            block.Dither = (byte)((nonZeroUv & 0xaaaa) > 0 ? 0 : q.Dither);

            return (nonZeroY | nonZeroUv) is 0;
        }

        private int GetCoeffs(Vp8BitReader br, Vp8BandProbas[] prob, int ctx, int[] dq, int n, Span<short> coeffs)
        {
            // Returns the position of the last non - zero coeff plus one.
            Vp8ProbaArray p = prob[n].Probabilities[ctx];
            for (; n < 16; ++n)
            {
                if (this.bitReader.GetBit((int)p.Probabilities[0]) is 0)
                {
                    // Previous coeff was last non - zero coeff.
                    return n;
                }

                // Sequence of zero coeffs.
                while (br.GetBit((int)p.Probabilities[1]) is 0)
                {
                    p = prob[++n].Probabilities[0];
                    if (n is 16)
                    {
                        return 16;
                    }
                }

                // Non zero coeffs.
                int v;
                if (br.GetBit((int)p.Probabilities[2]) is 0)
                {
                    v = 1;
                    p = prob[n + 1].Probabilities[1];
                }
                else
                {
                    v = this.GetLargeValue(p.Probabilities);
                    p = prob[n + 1].Probabilities[2];
                }

                int idx = n > 0 ? 1 : 0;
                coeffs[WebPConstants.Zigzag[n]] = (short)(br.ReadSignedValue(v) * dq[idx]);
            }

            return 16;
        }

        private int GetLargeValue(byte[] p)
        {
            // See section 13 - 2: http://tools.ietf.org/html/rfc6386#section-13.2
            int v;
            if (this.bitReader.GetBit(p[3]) is 0)
            {
                if (this.bitReader.GetBit(p[4]) is 0)
                {
                    v = 2;
                }
                else
                {
                    v = 3 + this.bitReader.GetBit(p[5]);
                }
            }
            else
            {
                if (this.bitReader.GetBit(p[6]) is 0)
                {
                    if (this.bitReader.GetBit(p[7]) is 0)
                    {
                        v = 5 + this.bitReader.GetBit(159);
                    }
                    else
                    {
                        v = 7 + (2 * this.bitReader.GetBit(165));
                        v += this.bitReader.GetBit(145);
                    }
                }
                else
                {
                    int bit1 = this.bitReader.GetBit(p[8]);
                    int bit0 = this.bitReader.GetBit(p[9] + bit1);
                    int cat = (2 * bit1) + bit0;
                    v = 0;
                    byte[] tab = null;
                    switch (cat)
                    {
                        case 0:
                            tab = WebPConstants.Cat3;
                            break;
                        case 1:
                            tab = WebPConstants.Cat4;
                            break;
                        case 2:
                            tab = WebPConstants.Cat5;
                            break;
                        case 3:
                            tab = WebPConstants.Cat6;
                            break;
                        default:
                            WebPThrowHelper.ThrowImageFormatException("VP8 parsing error");
                            break;
                    }

                    for (int i = 0; i < tab.Length; i++)
                    {
                        v += v + this.bitReader.GetBit(tab[i]);
                    }

                    v += 3 + (8 << cat);
                }
            }

            return v;
        }

        /// <summary>
        /// Paragraph 14.3: Implementation of the Walsh-Hadamard transform inversion.
        /// </summary>
        private void TransformWht(short[] input, short[] output)
        {
            var tmp = new int[16];
            for (int i = 0; i < 4; ++i)
            {
                int a0 = input[0 + i] + input[12 + i];
                int a1 = input[4 + i] + input[8 + i];
                int a2 = input[4 + i] - input[8 + i];
                int a3 = input[0 + i] - input[12 + i];
                tmp[0 + i] = a0 + a1;
                tmp[8 + i] = a0 - a1;
                tmp[4 + i] = a3 + a2;
                tmp[12 + i] = a3 - a2;
            }

            int outputOffset = 0;
            for (int i = 0; i < 4; ++i)
            {
                int dc = tmp[0 + (i * 4)] + 3;
                int a0 = dc + tmp[3 + (i * 4)];
                int a1 = tmp[1 + (i * 4)] + tmp[2 + (i * 4)];
                int a2 = tmp[1 + (i * 4)] - tmp[2 + (i * 4)];
                int a3 = dc - tmp[3 + (i * 4)];
                output[outputOffset + 0] = (short)((a0 + a1) >> 3);
                output[outputOffset + 16] = (short)((a3 + a2) >> 3);
                output[outputOffset + 32] = (short)((a0 - a1) >> 3);
                output[outputOffset + 48] = (short)((a3 - a2) >> 3);
                outputOffset += 64;
            }
        }

        private Vp8SegmentHeader ParseSegmentHeader(Vp8Proba proba)
        {
            var vp8SegmentHeader = new Vp8SegmentHeader
            {
                UseSegment = this.bitReader.ReadBool()
            };
            if (vp8SegmentHeader.UseSegment)
            {
                vp8SegmentHeader.UpdateMap = this.bitReader.ReadBool();
                bool updateData = this.bitReader.ReadBool();
                if (updateData)
                {
                    vp8SegmentHeader.Delta = this.bitReader.ReadBool();
                    bool hasValue;
                    for (int i = 0; i < vp8SegmentHeader.Quantizer.Length; i++)
                    {
                        hasValue = this.bitReader.ReadBool();
                        uint quantizeValue = hasValue ? this.bitReader.ReadValue(7) : 0;
                        vp8SegmentHeader.Quantizer[i] = (byte)quantizeValue;
                    }

                    for (int i = 0; i < vp8SegmentHeader.FilterStrength.Length; i++)
                    {
                        hasValue = this.bitReader.ReadBool();
                        uint filterStrengthValue = hasValue ? this.bitReader.ReadValue(6) : 0;
                        vp8SegmentHeader.FilterStrength[i] = (byte)filterStrengthValue;
                    }

                    if (vp8SegmentHeader.UpdateMap)
                    {
                        for (int s = 0; s < proba.Segments.Length; ++s)
                        {
                            hasValue = this.bitReader.ReadBool();
                            proba.Segments[s] = hasValue ? this.bitReader.ReadValue(8) : 255;
                        }
                    }
                }
            }
            else
            {
                vp8SegmentHeader.UpdateMap = false;
            }

            return vp8SegmentHeader;
        }

        private Vp8FilterHeader ParseFilterHeader()
        {
            var vp8FilterHeader = new Vp8FilterHeader();
            vp8FilterHeader.LoopFilter = this.bitReader.ReadBool() ? LoopFilter.Simple : LoopFilter.Complex;
            vp8FilterHeader.Level = (int)this.bitReader.ReadValue(6);
            vp8FilterHeader.Sharpness = (int)this.bitReader.ReadValue(3);
            vp8FilterHeader.UseLfDelta = this.bitReader.ReadBool();

            // TODO: use enum here?
            // 0 = 0ff, 1 = simple, 2 = complex
            int filterType = (vp8FilterHeader.Level is 0) ? 0 : vp8FilterHeader.LoopFilter is LoopFilter.Simple ? 1 : 2;
            if (vp8FilterHeader.UseLfDelta)
            {
                // Update lf-delta?
                if (this.bitReader.ReadBool())
                {
                    bool hasValue;
                    for (int i = 0; i < vp8FilterHeader.RefLfDelta.Length; i++)
                    {
                        hasValue = this.bitReader.ReadBool();
                        if (hasValue)
                        {
                            vp8FilterHeader.RefLfDelta[i] = this.bitReader.ReadSignedValue(6);
                        }
                    }

                    for (int i = 0; i < vp8FilterHeader.ModeLfDelta.Length; i++)
                    {
                        hasValue = this.bitReader.ReadBool();
                        if (hasValue)
                        {
                            vp8FilterHeader.ModeLfDelta[i] = this.bitReader.ReadSignedValue(6);
                        }
                    }
                }
            }

            return vp8FilterHeader;
        }

        private void ParsePartitions(Vp8Decoder dec)
        {
            uint size = this.bitReader.Remaining - this.bitReader.PartitionLength;
            int startIdx = (int)this.bitReader.PartitionLength;
            Span<byte> sz = this.bitReader.Data.AsSpan(startIdx);
            int sizeLeft = (int)size;
            int numPartsMinusOne = (1 << (int)this.bitReader.ReadValue(2)) - 1;
            int lastPart = numPartsMinusOne;

            int partStart = startIdx + (lastPart * 3);
            sizeLeft -= lastPart * 3;
            for (int p = 0; p < lastPart; ++p)
            {
                int pSize = sz[0] | (sz[1] << 8) | (sz[2] << 16);
                if (pSize > sizeLeft)
                {
                    pSize = sizeLeft;
                }

                dec.Vp8BitReaders[p] = new Vp8BitReader(this.bitReader.Data, (uint)pSize, partStart);
                partStart += pSize;
                sizeLeft -= pSize;
                sz = sz.Slice(3);
            }

            dec.Vp8BitReaders[lastPart] = new Vp8BitReader(this.bitReader.Data, (uint)sizeLeft, partStart);
        }

        private void ParseDequantizationIndices(Vp8SegmentHeader vp8SegmentHeader)
        {
            int baseQ0 = (int)this.bitReader.ReadValue(7);
            bool hasValue = this.bitReader.ReadBool();
            int dqy1Dc = hasValue ? this.bitReader.ReadSignedValue(4) : 0;
            hasValue = this.bitReader.ReadBool();
            int dqy2Dc = hasValue ? this.bitReader.ReadSignedValue(4) : 0;
            hasValue = this.bitReader.ReadBool();
            int dqy2Ac = hasValue ? this.bitReader.ReadSignedValue(4) : 0;
            hasValue = this.bitReader.ReadBool();
            int dquvDc = hasValue ? this.bitReader.ReadSignedValue(4) : 0;
            hasValue = this.bitReader.ReadBool();
            int dquvAc = hasValue ? this.bitReader.ReadSignedValue(4) : 0;
            for (int i = 0; i < WebPConstants.NumMbSegments; ++i)
            {
                int q;
                if (vp8SegmentHeader.UseSegment)
                {
                    q = vp8SegmentHeader.Quantizer[i];
                    if (!vp8SegmentHeader.Delta)
                    {
                        q += baseQ0;
                    }
                }
                else
                {
                    if (i > 0)
                    {
                        // dec->dqm_[i] = dec->dqm_[0];
                        continue;
                    }
                    else
                    {
                        q = baseQ0;
                    }
                }

                var m = new Vp8QuantMatrix();
                m.Y1Mat[0] = WebPConstants.DcTable[Clip(q + dqy1Dc, 127)];
                m.Y1Mat[1] = WebPConstants.AcTable[Clip(q + 0, 127)];
                m.Y2Mat[0] = WebPConstants.DcTable[Clip(q + dqy2Dc, 127)] * 2;

                // For all x in [0..284], x*155/100 is bitwise equal to (x*101581) >> 16.
                // The smallest precision for that is '(x*6349) >> 12' but 16 is a good word size.
                m.Y2Mat[1] = (WebPConstants.AcTable[Clip(q + dqy2Ac, 127)] * 101581) >> 16;
                if (m.Y2Mat[1] < 8)
                {
                    m.Y2Mat[1] = 8;
                }

                m.UvMat[0] = WebPConstants.DcTable[Clip(q + dquvDc, 117)];
                m.UvMat[1] = WebPConstants.AcTable[Clip(q + dquvAc, 127)];

                // For dithering strength evaluation.
                m.UvQuant = q + dquvAc;
            }
        }

        private void ParseProbabilities(Vp8Decoder dec, Vp8Proba proba)
        {
            for (int t = 0; t < WebPConstants.NumTypes; ++t)
            {
                for (int b = 0; b < WebPConstants.NumBands; ++b)
                {
                    for (int c = 0; c < WebPConstants.NumCtx; ++c)
                    {
                        for (int p = 0; p < WebPConstants.NumProbas; ++p)
                        {
                            byte prob = WebPConstants.CoeffsUpdateProba[t, b, c, p];
                            int v = this.bitReader.GetBit(prob) > 0
                                        ? (int)this.bitReader.ReadValue(8)
                                        : WebPConstants.DefaultCoeffsProba[t, b, c, p];
                            proba.Bands[t, b].Probabilities[c].Probabilities[p] = (byte)v;
                        }
                    }
                }

                for (int b = 0; b < 16 + 1; ++b)
                {
                    proba.BandsPtr[t, b] = proba.Bands[t, WebPConstants.Bands[b]];
                }
            }

            dec.UseSkipProbability = this.bitReader.ReadBool();
            if (dec.UseSkipProbability)
            {
                dec.SkipProbability = (byte)this.bitReader.ReadValue(8);
            }
        }

        static bool Is8bOptimizable(Vp8LMetadata hdr)
        {
            int i;
            if (hdr.ColorCacheSize > 0)
            {
                return false;
            }

            // When the Huffman tree contains only one symbol, we can skip the
            // call to ReadSymbol() for red/blue/alpha channels.
            for (i = 0; i < hdr.NumHTreeGroups; ++i)
            {
                List<HuffmanCode[]> htrees = hdr.HTreeGroups[i].HTrees;
                if (htrees[HuffIndex.Red][0].Value > 0)
                {
                    return false;
                }

                if (htrees[HuffIndex.Blue][0].Value > 0)
                {
                    return false;
                }

                if (htrees[HuffIndex.Alpha][0].Value > 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static uint NzCodeBits(uint nzCoeffs, int nz, int dcNz)
        {
            nzCoeffs <<= 2;
            nzCoeffs |= (uint)((nz > 3) ? 3 : (nz > 1) ? 2 : dcNz);
            return nzCoeffs;
        }

        private static Vp8BandProbas[] GetBandsRow(Vp8BandProbas[,] bands, int rowIdx)
        {
            Vp8BandProbas[] bandsRow = Enumerable.Range(0, bands.GetLength(1)).Select(x => bands[rowIdx, x]).ToArray();
            return bandsRow;
        }

        private static int Clip(int value, int max)
        {
            return value < 0 ? 0 : value > max ? max : value;
        }
    }

    struct YUVPixel
    {
        public byte Y { get; }

        public byte U { get; }

        public byte V { get; }
    }
}
