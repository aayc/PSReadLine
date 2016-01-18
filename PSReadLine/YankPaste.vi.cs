﻿/********************************************************************++
Copyright (c) Microsoft Corporation.  All rights reserved.
--********************************************************************/

using System;
using System.Collections.Generic;
using System.Management.Automation.Language;
using System.Windows;

namespace Microsoft.PowerShell
{
    public partial class PSConsoleReadLine
    {
        private string _clipboard = string.Empty;

        public static void PasteAfter(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (string.IsNullOrEmpty(_singleton._clipboard))
            {
                Ding();
                return;
            }

            _singleton.PasteAfterImpl();
        }

        public static void PasteBefore(ConsoleKeyInfo? key = null, object arg = null)
        {
            if (string.IsNullOrEmpty(_singleton._clipboard))
            {
                Ding();
                return;
            }
            _singleton.PasteBeforeImpl();
        }

        private void PasteAfterImpl()
        {
            if (_current < _buffer.Length)
            {
                _current++;
            }
            Insert(_clipboard);
            _current--;
            Render();
        }

        private void PasteBeforeImpl()
        {
            Insert(_clipboard);
            _current--;
            Render();
        }

        private void SaveToClipboard(int startIndex, int length)
        {
            _clipboard = _buffer.ToString(startIndex, length);
        }

        public static void ViYankLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            _singleton.SaveToClipboard(0, _singleton._buffer.Length);
        }

        public static void ViYankRight(ConsoleKeyInfo? key = null, object arg = null)
        {
            int numericArg;
            if (!TryGetArgAsInt(arg, out numericArg, 1))
            {
                return;
            }

            int start = _singleton._current;
            int length = 0;

            while (numericArg-- > 0)
            {
                length++;
            }

            _singleton.SaveToClipboard(start, length);
        }

        public static void ViYankLeft(ConsoleKeyInfo? key = null, object arg = null)
        {
            int numericArg;
            if (!TryGetArgAsInt(arg, out numericArg, 1))
            {
                return;
            }

            int start = _singleton._current;
            if (start == 0)
            {
                _singleton.SaveToClipboard(start, 1);
                return;
            }

            int length = 0;

            while (numericArg-- > 0)
            {
                if (start > 0)
                {
                    start--;
                    length++;
                }
            }

            _singleton.SaveToClipboard(start, length);
        }

        public static void ViYankToEndOfLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            int start = _singleton._current;
            int length = _singleton._buffer.Length - _singleton._current;
            _singleton.SaveToClipboard(start, length);
        }

        public static void ViYankPreviousWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            int numericArg;
            if (!TryGetArgAsInt(arg, out numericArg, 1))
            {
                return;
            }

            int start = _singleton._current;

            while (numericArg-- > 0)
            {
                start = _singleton.ViFindPreviousWordPoint(start, _singleton.Options.WordDelimiters);
            }

            int length = _singleton._current - start;
            if (length > 0)
            {
                _singleton.SaveToClipboard(start, length);
            }
        }

        public static void ViYankNextWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            int numericArg;
            if (!TryGetArgAsInt(arg, out numericArg, 1))
            {
                return;
            }

            int end = _singleton._current;

            while (numericArg-- > 0)
            {
                end = _singleton.ViFindNextWordPoint(end, _singleton.Options.WordDelimiters);
            }

            int length = end - _singleton._current;
            //if (_singleton.IsAtEndOfLine(end))
            //{
            //    length++;
            //}
            if (length > 0)
            {
                _singleton.SaveToClipboard(_singleton._current, length);
            }
        }

        public static void ViYankEndOfWord(ConsoleKeyInfo? key = null, object arg = null)
        {
            int numericArg;
            if (!TryGetArgAsInt(arg, out numericArg, 1))
            {
                return;
            }

            int end = _singleton._current;

            while (numericArg-- > 0)
            {
                end = _singleton.ViFindNextWordEnd(end, _singleton.Options.WordDelimiters);
            }

            int length = 1 + end - _singleton._current;
            if (length > 0)
            {
                _singleton.SaveToClipboard(_singleton._current, length);
            }
        }

        public static void ViYankEndOfGlob(ConsoleKeyInfo? key = null, object arg = null)
        {
            int numericArg;
            if (!TryGetArgAsInt(arg, out numericArg, 1))
            {
                return;
            }

            int end = _singleton._current;

            while (numericArg-- > 0)
            {
                end = _singleton.ViFindGlobEnd(end);
            }

            int length = 1 + end - _singleton._current;
            if (length > 0)
            {
                _singleton.SaveToClipboard(_singleton._current, length);
            }
        }

        public static void ViYankBeginningOfLine(ConsoleKeyInfo? key = null, object arg = null)
        {
            int length = _singleton._current;
            if (length > 0)
            {
                _singleton.SaveToClipboard(0, length);
            }
        }

        public static void ViYankToFirstChar(ConsoleKeyInfo? key = null, object arg = null)
        {
            int start = 0;
            while (_singleton.IsWhiteSpace(start))
            {
                start++;
            }
            if (start == _singleton._current)
            {
                return;
            }

            int length = _singleton._current - start;
            if (length > 0)
            {
                _singleton.SaveToClipboard(start, length);
            }
        }

        public static void ViYankPercent(ConsoleKeyInfo? key = null, object arg = null)
        {
            int start = _singleton.ViFindBrace(_singleton._current);
            if (_singleton._current < start)
            {
                _singleton.SaveToClipboard(_singleton._current, start - _singleton._current + 1);
            }
            else if (start < _singleton._current)
            {
                _singleton.SaveToClipboard(start, _singleton._current - start + 1);
            }
            else
            {
                Ding();
            }
        }

        public static void ViYankPreviousGlob(ConsoleKeyInfo? key = null, object arg = null)
        {
            int numericArg;
            if (!TryGetArgAsInt(arg, out numericArg, 1))
            {
                return;
            }

            int start = _singleton._current;
            while (numericArg-- > 0)
            {
                start = _singleton.ViFindPreviousGlob(start - 1);
            }
            if (start < _singleton._current)
            {
                _singleton.SaveToClipboard(start, _singleton._current - start);
            }
            else
            {
                Ding();
            }
        }

        public static void ViYankNextGlob(ConsoleKeyInfo? key = null, object arg = null)
        {
            int numericArg;
            if (!TryGetArgAsInt(arg, out numericArg, 1))
            {
                return;
            }

            int end = _singleton._current;
            while (numericArg-- > 0)
            {
                end = _singleton.ViFindNextGlob(end);
            }
            _singleton.SaveToClipboard(_singleton._current, end - _singleton._current);
        }
    }
}
