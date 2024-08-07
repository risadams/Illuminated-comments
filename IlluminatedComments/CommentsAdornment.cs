﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;

namespace IlluminatedComments
{
    /// <summary>
    ///     CommentsAdornment places red boxes behind all the "a"s in the editor window
    /// </summary>
    internal sealed class CommentsAdornment : ITagger<ErrorTag>, IDisposable
    {
        private string _contentTypeName;
        private readonly ConcurrentDictionary<int, ITextViewLine> _editedLines = new ConcurrentDictionary<int, ITextViewLine>();
        private readonly List<ITagSpan<ErrorTag>> _errorTags;
        private bool _initialized1;
        private bool _initialized2;

        private readonly IAdornmentLayer _layer;

        private readonly List<string> _processingUris = new List<string>();
        private readonly ITextDocumentFactoryService _textDocumentFactory;

        private readonly Timer _timer = new Timer(200);
        private readonly ConcurrentDictionary<WebClient, ImageParameters> _toAddImages = new ConcurrentDictionary<WebClient, ImageParameters>();
        private readonly VariableExpander _variableExpander;
        private readonly IWpfTextView _view;

        /// <summary>
        ///     Initializes a new instance of the <see cref="CommentsAdornment" /> class.
        /// </summary>
        /// <param name="view">Text view to create the adornment for</param>
        static CommentsAdornment() => Enabled = true;

        public CommentsAdornment(IWpfTextView view, ITextDocumentFactoryService textDocumentFactory, SVsServiceProvider serviceProvider)
        {
            _textDocumentFactory =  textDocumentFactory;
            _view                =  view;
            _layer               =  view.GetAdornmentLayer("CommentImageAdornmentLayer");
            Images               =  new ConcurrentDictionary<int, CommentImage>();
            _view.LayoutChanged  += OnLayoutChanged;

            _contentTypeName                    =  view.TextBuffer.ContentType.TypeName;
            _view.TextBuffer.ContentTypeChanged += OnContentTypeChanged;

            _errorTags        = new List<ITagSpan<ErrorTag>>();
            _variableExpander = new VariableExpander(_view, serviceProvider);

            _timer.Elapsed += On_Timer_Elapsed;
        }

        public static bool Enabled { get; set; }

        public ConcurrentDictionary<int, CommentImage> Images { get; set; }

        public static void ToggleEnabled()
        {
            Enabled = !Enabled;
            var message = "Illuminated comments " + (Enabled ? "enabled" : "disabled") + ". Scroll editor window(s) to update.";
            UIMessage.Show(message);
        }

        private void OnContentTypeChanged(object sender, ContentTypeChangedEventArgs e)
        {
            _contentTypeName = e.AfterContentType.TypeName;
        }

        internal void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            try
            {
                if (!Enabled)
                    return;

                _errorTags.Clear();
                TagsChanged?.Invoke(
                    this,
                    new SnapshotSpanEventArgs(
                        new SnapshotSpan(
                            _view.TextSnapshot,
                            new Span(0, _view.TextSnapshot.Length))));

                foreach (var line in e.NewOrReformattedLines)
                {
                    var lineNumber = line.Snapshot.GetLineFromPosition(line.Start.Position).LineNumber;

                    _editedLines[lineNumber] = line;
                }

                ResetTimer();

                // Sometimes, on loading a file in an editor view, the line transform gets triggered before the image adornments
                // have been added, so the lines don't resize to the image height. So here's a workaround:
                // Changing the zoom level triggers the required update.
                // Need to do it twice - once to trigger the event, and again to change it back to the user's expected level.
                if (!_initialized1)
                {
                    _view.ZoomLevel++;
                    _initialized1 = true;
                }

                if (!_initialized2)
                {
                    _view.ZoomLevel--;
                    _initialized2 = true;
                }
            }
            catch (Exception ex)
            {
                ExceptionHandler.Notify(ex, true);
            }
        }

        private void ResetTimer()
        {
            _timer.Stop();
            _timer.Start();
        }

        private async void On_Timer_Elapsed(object sender, ElapsedEventArgs e)
        {
            _timer.Stop();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                string filepath = null;
                if (_textDocumentFactory != null &&
                    _textDocumentFactory.TryGetTextDocument(_view.TextBuffer, out var textDocument))
                    filepath = textDocument.FilePath;

                foreach (var kvp in _editedLines)
                {
                    try
                    {
                        CreateVisuals(kvp.Value, kvp.Key, filepath);
                    }
                    catch (InvalidOperationException ex)
                    {
                        ExceptionHandler.Notify(ex, true);
                    }
                }

                _editedLines.Clear();
            });
        }

        private void CreateVisuals(ITextViewLine line, int lineNumber, string filepath)
        {
            try
            {
                var lineText = line.Extent.GetText();
                var lines = lineText.Split(
                    new[] {Environment.NewLine},
                    StringSplitOptions.RemoveEmptyEntries);
                // multiline mean a block of code is collapsed
                // do not display pics from the collapsed text
                if (lines.Length > 1)
                    return;
                var matchIndex = CommentImageParser.Match(_contentTypeName, lineText, out var matchedText);
                if (matchIndex >= 0)
                {
                    // Get coordinates of text
                    var start = line.Extent.Start.Position + matchIndex;
                    var end   = line.Start + (line.Extent.Length - 1);
                    var span  = new SnapshotSpan(_view.TextSnapshot, Span.FromBounds(start, end));

                    CommentImageParser.TryParse(
                        matchedText,
                        out var imageUrl, out var scale, out var xmlParseException);

                    if (xmlParseException != null)
                    {
                        if (Images.TryRemove(lineNumber, out var commentImage))
                        {
                            _layer.RemoveAdornment(commentImage);
                            commentImage.Dispose();
                        }

                        _errorTags.Add(
                            new TagSpan<ErrorTag>(
                                span,
                                new ErrorTag("XML parse error", GetErrorMessage(xmlParseException))));

                        return;
                    }

                    var reload = false;
                    var image = Images.AddOrUpdate(lineNumber, ln =>
                    {
                        reload = true;
                        return new CommentImage(_variableExpander);
                    }, (ln, img) =>
                    {
                        if (img.OriginalUrl == imageUrl && (Math.Abs(img.Scale - scale) > float.Epsilon))
                        {
                            // URL same but scale changed
                            img.Scale = scale;
                            reload    = true;
                        }
                        else if (img.OriginalUrl != imageUrl)
                        {
                            // URL different, must load from new source
                            reload = true;
                        }

                        return img;
                    });

                    var originalUrl = imageUrl;
                    if (reload)
                    {
                        if (_processingUris.Contains(imageUrl)) return;

                        if (imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            if (ImageCache.Instance.TryGetValue(imageUrl, out var localPath))
                            {
                                imageUrl = localPath;
                            }
                            else
                            {
                                _processingUris.Add(imageUrl);
                                var tempPath = Path.Combine(Path.GetTempPath(), Path.GetFileName(imageUrl));
                                var client   = new WebClient();
                                client.DownloadDataCompleted += Client_DownloadDataCompleted;

                                _toAddImages.TryAdd(
                                    client,
                                    new ImageParameters
                                    {
                                        Uri        = imageUrl,
                                        LocalPath  = tempPath,
                                        Image      = image,
                                        Line       = line,
                                        LineNumber = lineNumber,
                                        Span       = span,
                                        Scale      = scale,
                                        Filepath   = filepath
                                    });

                                client.DownloadDataAsync(new Uri(imageUrl));

                                return;
                            }
                        }
                    }

                    if (imageUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        if (ImageCache.Instance.TryGetValue(imageUrl, out var localPath))
                            imageUrl = localPath;
                    ProcessImage(image, imageUrl, originalUrl, line, lineNumber, span, scale, filepath);
                }
                else
                {
                    Images.TryRemove(lineNumber, out var commentImage);
                    commentImage?.Dispose();
                }
            }
            catch (Exception ex)
            {
                ExceptionHandler.Notify(ex, true);
            }
        }

        private void Client_DownloadDataCompleted(object sender, DownloadDataCompletedEventArgs e)
        {
            try
            {
                if (sender is WebClient client)
                {
                    client.DownloadDataCompleted -= Client_DownloadDataCompleted;

                    if (_toAddImages.TryGetValue(client, out var item))
                    {
                        var data = e.Result;
                        File.WriteAllBytes(item.LocalPath, data);
                        ImageCache.Instance.Add(item.Uri, item.LocalPath);
                        _processingUris.Remove(item.Uri);

                        ProcessImage(item.Image,
                            item.LocalPath,
                            item.Uri,
                            item.Line,
                            item.LineNumber,
                            item.Span,
                            item.Scale,
                            item.Filepath);

                        _toAddImages.TryRemove(client, out var value);
                    }
                }
            }
            catch (Exception ex)
            {
                ExceptionHandler.Notify(ex, true);
            }
        }

        private void ProcessImage(CommentImage image, string imageUrl, string originalUrl, ITextViewLine line, int lineNumber, SnapshotSpan span, double scale, string filepath)
        {
            try
            {
                var result = image.TrySet(
                    imageUrl,
                    originalUrl,
                    scale,
                    filepath,
                    out var imageLoadingException);

                // Position image and add as adornment
                if (imageLoadingException == null)
                {
                    AddAdornment(image, line, span);
                }
                else
                {
                    Images.TryRemove(lineNumber, out var commentImage);
                    commentImage.Dispose();

                    _errorTags.Add(
                        new TagSpan<ErrorTag>(
                            span,
                            new ErrorTag("Trouble loading image", GetErrorMessage(imageLoadingException))));
                }
            }
            catch (Exception ex)
            {
                ExceptionHandler.Notify(ex, true);
            }
        }

        private void AddAdornment(UIElement element, ITextViewLine line, SnapshotSpan span)
        {
            Geometry geometry = null;
            try
            {
                geometry = _view.TextViewLines.GetMarkerGeometry(span);
            }
            catch { }

            if (geometry == null)
                // Exceptional case when image dimensions are massive (e.g. specifying very large scale factor)
                throw new InvalidOperationException("Couldn't get source code line geometry. Is the loaded image massive?");

            try
            {
                Canvas.SetLeft(element, geometry.Bounds.Left);
                Canvas.SetTop(element, line.TextBottom);
            }
            catch { }

            // Add element to the editor view
            try
            {
                _layer.RemoveAdornment(element);
                _layer.AddAdornment(
                    AdornmentPositioningBehavior.TextRelative,
                    line.Extent,
                    null,
                    element,
                    null);
            }
            catch (Exception ex)
            {
                // No expected exceptions, so tell user something is wrong.
                ExceptionHandler.Notify(ex, true);
            }
        }

        private static string GetErrorMessage(Exception exception)
        {
            Trace.WriteLine("Problem parsing comment text or loading image...\n" + exception);

            string message;
            if (exception is XmlException)
                message = "Problem with comment format: " + exception.Message;
            else if (exception is NotSupportedException)
                message = exception.Message + "\nThis problem could be caused by a corrupt, invalid or unsupported image file.";
            else
                message = exception.Message;
            return message;
        }

        private void UnsubscribeFromViewerEvents()
        {
            _view.LayoutChanged                 -= OnLayoutChanged;
            _view.TextBuffer.ContentTypeChanged -= OnContentTypeChanged;
        }

        private class ImageParameters
        {
            public string Uri { get; set; }
            public string LocalPath { get; set; }
            public CommentImage Image { get; set; }
            public ITextViewLine Line { get; set; }
            public int LineNumber { get; set; }
            public SnapshotSpan Span { get; set; }
            public double Scale { get; set; }
            public string Filepath { get; set; }
        }

        #region ITagger<ErrorTag> Members

        public IEnumerable<ITagSpan<ErrorTag>> GetTags(NormalizedSnapshotSpanCollection spans) => _errorTags;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        #region IDisposable Support

        private bool disposedValue; // To detect redundant calls

        private void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    UnsubscribeFromViewerEvents();
                    _timer.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~CommentsAdornment() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }

        #endregion

        #endregion
    }
}