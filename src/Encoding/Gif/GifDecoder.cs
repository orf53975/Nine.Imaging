﻿namespace Nine.Imaging.Encoding
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using Nine.Imaging.Gif;

    public class GifDecoder : IImageDecoder
    {
        public int HeaderSize => 6;

        public bool IsSupportedFileExtension(string extension)
        {
            if (extension.StartsWith(".")) extension = extension.Substring(1);
            return extension.Equals("GIF", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsSupportedFileFormat(byte[] header)
        {
            return header.Length >= 6 &&
                   header[0] == 0x47 && // G
                   header[1] == 0x49 && // I
                   header[2] == 0x46 && // F
                   header[3] == 0x38 && // 8
                  (header[4] == 0x39 || header[4] == 0x37) && // 9 or 7
                   header[5] == 0x61;   // a
        }

        Image IImageDecoder.Decode(Stream stream) => Decode(stream);

        public AnimatedImage Decode(Stream stream) => new GifDecoderCore().Decode(stream);

        struct GifDecoderCore
        {
            private const byte ExtensionIntroducer = 0x21;
            private const byte Terminator = 0;
            private const byte ImageLabel = 0x2C;
            private const byte EndIntroducer = 0x3B;
            private const byte ApplicationExtensionLabel = 0xFF;
            private const byte CommentLabel = 0xFE;
            private const byte ImageDescriptorLabel = 0x2C;
            private const byte PlainTextLabel = 0x01;
            private const byte GraphicControlLabel = 0xF9;

            private Stream _stream;
            private GifLogicalScreenDescriptor _descriptor;
            private byte[] _globalColorTable;
            private byte[] _currentFrame;
            private List<Image> _frames;
            private GifGraphicsControlExtension _graphicsControl;
            
            public AnimatedImage Decode(Stream stream)
            {
                _stream = stream;
                _stream.Seek(6, SeekOrigin.Current);
                _frames = new List<Image>();

                ReadLogicalScreenDescriptor();

                if (_descriptor.GlobalColorTableFlag)
                {
                    _globalColorTable = new byte[_descriptor.GlobalColorTableSize * 3];

                    // Read the global color table from the stream
                    stream.Read(_globalColorTable, 0, _globalColorTable.Length);
                }

                int nextFlag = stream.ReadByte();
                while (nextFlag != 0)
                {
                    if (nextFlag == ImageLabel)
                    {
                        ReadFrame();
                    }
                    else if (nextFlag == ExtensionIntroducer)
                    {
                        int gcl = stream.ReadByte();
                        switch (gcl)
                        {
                            case GraphicControlLabel:
                                ReadGraphicalControlExtension();
                                break;
                            case CommentLabel:
                                ReadComments();
                                break;
                            case ApplicationExtensionLabel:
                                Skip(12);
                                break;
                            case PlainTextLabel:
                                Skip(13);
                                break;
                        }
                    }
                    else if (nextFlag == EndIntroducer)
                    {
                        break;
                    }
                    nextFlag = stream.ReadByte();
                }


                // If not 0, this field specifies the number of hundredths (1/100) of a second to 
                // wait before continuing with the processing of the Data Stream. 
                var frameDuration = _graphicsControl != null ? _graphicsControl.DelayTime * 10 : 0;

                return new AnimatedImage(frameDuration, _frames);
            }

            private void ReadGraphicalControlExtension()
            {
                byte[] buffer = new byte[6];

                _stream.Read(buffer, 0, buffer.Length);

                byte packed = buffer[1];

                _graphicsControl = new GifGraphicsControlExtension();
                _graphicsControl.DelayTime = BitConverter.ToInt16(buffer, 2);
                _graphicsControl.TransparencyIndex = buffer[4];
                _graphicsControl.TransparencyFlag = (packed & 0x01) == 1;
                _graphicsControl.DisposalMethod = (DisposalMethod)((packed & 0x1C) >> 2);
            }

            private GifImageDescriptor ReadImageDescriptor()
            {
                byte[] buffer = new byte[9];

                _stream.Read(buffer, 0, buffer.Length);

                byte packed = buffer[8];

                GifImageDescriptor imageDescriptor = new GifImageDescriptor();
                imageDescriptor.Left = BitConverter.ToInt16(buffer, 0);
                imageDescriptor.Top = BitConverter.ToInt16(buffer, 2);
                imageDescriptor.Width = BitConverter.ToInt16(buffer, 4);
                imageDescriptor.Height = BitConverter.ToInt16(buffer, 6);
                imageDescriptor.LocalColorTableFlag = ((packed & 0x80) >> 7) == 1;
                imageDescriptor.LocalColorTableSize = 2 << (packed & 0x07);
                imageDescriptor.InterlaceFlag = ((packed & 0x40) >> 6) == 1;

                return imageDescriptor;
            }

            private void ReadLogicalScreenDescriptor()
            {
                byte[] buffer = new byte[7];

                _stream.Read(buffer, 0, buffer.Length);

                byte packed = buffer[4];

                _descriptor = new GifLogicalScreenDescriptor();
                _descriptor.Width = BitConverter.ToInt16(buffer, 0);
                _descriptor.Height = BitConverter.ToInt16(buffer, 2);
                _descriptor.Background = buffer[5];
                _descriptor.GlobalColorTableFlag = ((packed & 0x80) >> 7) == 1;
                _descriptor.GlobalColorTableSize = 2 << (packed & 0x07);

                if (_descriptor.GlobalColorTableSize > 255 * 4)
                {
                    throw new ImageFormatException($"Invalid gif colormap size '{ _descriptor.GlobalColorTableSize }'");
                }

                if (_descriptor.Width > Image.MaxWidth || _descriptor.Height > Image.MaxHeight)
                {
                    throw new ArgumentOutOfRangeException(
                        $"The input gif '{ _descriptor.Width }x{ _descriptor.Height }' is bigger then the max allowed size '{ Image.MaxWidth }x{ Image.MaxHeight }'");
                }
            }

            private void Skip(int length)
            {
                _stream.Seek(length, SeekOrigin.Current);

                int flag = 0;

                while ((flag = _stream.ReadByte()) != 0)
                {
                    _stream.Seek(flag, SeekOrigin.Current);
                }
            }

            private void ReadComments()
            {
                int flag = 0;

                while ((flag = _stream.ReadByte()) != 0)
                {
                    _stream.Seek(flag, SeekOrigin.Current);
                }
            }

            private void ReadFrame()
            {
                GifImageDescriptor imageDescriptor = ReadImageDescriptor();

                byte[] localColorTable = ReadFrameLocalColorTable(imageDescriptor);

                byte[] indices = ReadFrameIndices(imageDescriptor);

                // Determine the color table for this frame. If there is a local one, use it
                // otherwise use the global color table.
                byte[] colorTable = localColorTable != null ? localColorTable : _globalColorTable;

                ReadFrameColors(indices, colorTable, imageDescriptor);

                Skip(0); // skip any remaining blocks
            }

            private byte[] ReadFrameIndices(GifImageDescriptor imageDescriptor)
            {
                int dataSize = _stream.ReadByte();

                LZWDecoder lzwDecoder = new LZWDecoder(_stream);

                byte[] indices = lzwDecoder.DecodePixels(imageDescriptor.Width, imageDescriptor.Height, dataSize);
                return indices;
            }

            private byte[] ReadFrameLocalColorTable(GifImageDescriptor imageDescriptor)
            {
                byte[] localColorTable = null;

                if (imageDescriptor.LocalColorTableFlag == true)
                {
                    localColorTable = new byte[imageDescriptor.LocalColorTableSize * 3];

                    _stream.Read(localColorTable, 0, localColorTable.Length);
                }

                return localColorTable;
            }

            private void ReadFrameColors(byte[] indices, byte[] colorTable, GifImageDescriptor descriptor)
            {
                int imageWidth = _descriptor.Width;
                int imageHeight = _descriptor.Height;

                if (_currentFrame == null)
                {
                    _currentFrame = new byte[imageWidth * imageHeight * 4];
                }

                byte[] lastFrame = null;

                if (_graphicsControl != null &&
                    _graphicsControl.DisposalMethod == DisposalMethod.RestoreToPrevious)
                {
                    lastFrame = new byte[imageWidth * imageHeight * 4];

                    Array.Copy(_currentFrame, lastFrame, lastFrame.Length);
                }

                int offset = 0, i = 0, index = -1;

                int iPass = 0; // the interlace pass
                int iInc = 8; // the interlacing line increment
                int iY = 0; // the current interlaced line
                int writeY = 0; // the target y offset to write to

                for (int y = descriptor.Top; y < descriptor.Top + descriptor.Height; y++)
                {
                    // Check if this image is interlaced.
                    if (descriptor.InterlaceFlag)
                    {
                        // If so then we read lines at predetermined offsets.
                        // When an entire image height worth of offset lines has been read we consider this a pass.
                        // With each pass the number of offset lines changes and the starting line changes.
                        if (iY >= descriptor.Height)
                        {
                            iPass++;
                            switch (iPass)
                            {
                                case 1:
                                    iY = 4;
                                    break;
                                case 2:
                                    iY = 2;
                                    iInc = 4;
                                    break;
                                case 3:
                                    iY = 1;
                                    iInc = 2;
                                    break;
                            }
                        }

                        writeY = iY + descriptor.Top;

                        iY += iInc;
                    }
                    else
                    {
                        writeY = y;
                    }

                    for (int x = descriptor.Left; x < descriptor.Left + descriptor.Width; x++)
                    {
                        offset = writeY * imageWidth + x;

                        index = indices[i];

                        if (_graphicsControl == null ||
                            _graphicsControl.TransparencyFlag == false ||
                            _graphicsControl.TransparencyIndex != index)
                        {
                            _currentFrame[offset * 4 + 0] = colorTable[index * 3 + 2];
                            _currentFrame[offset * 4 + 1] = colorTable[index * 3 + 1];
                            _currentFrame[offset * 4 + 2] = colorTable[index * 3 + 0];
                            _currentFrame[offset * 4 + 3] = (byte)255;
                        }

                        i++;
                    }
                }

                var pixels = new byte[imageWidth * imageHeight * 4];

                Array.Copy(_currentFrame, pixels, pixels.Length);

                _frames.Add(new Image(imageWidth, imageHeight, pixels));

                if (_graphicsControl != null)
                {
                    if (_graphicsControl.DisposalMethod == DisposalMethod.RestoreToBackground)
                    {
                        for (int y = descriptor.Top; y < descriptor.Top + descriptor.Height; y++)
                        {
                            for (int x = descriptor.Left; x < descriptor.Left + descriptor.Width; x++)
                            {
                                offset = y * imageWidth + x;

                                _currentFrame[offset * 4 + 0] = 0;
                                _currentFrame[offset * 4 + 1] = 0;
                                _currentFrame[offset * 4 + 2] = 0;
                                _currentFrame[offset * 4 + 3] = 0;
                            }
                        }
                    }
                    else if (_graphicsControl.DisposalMethod == DisposalMethod.RestoreToPrevious)
                    {
                        _currentFrame = lastFrame;
                    }
                }
            }
        }
    }
}
