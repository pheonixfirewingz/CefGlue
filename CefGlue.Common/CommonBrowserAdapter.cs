using System;
using System.Threading.Tasks;
using Xilium.CefGlue.Common.Events;
using Xilium.CefGlue.Common.Handlers;
using Xilium.CefGlue.Common.Helpers;
using Xilium.CefGlue.Common.Helpers.Logger;
using Xilium.CefGlue.Common.JavascriptExecution;
using Xilium.CefGlue.Common.ObjectBinding;
using Xilium.CefGlue.Common.Platform;
using Xilium.CefGlue.Common.RendererProcessCommunication;

namespace Xilium.CefGlue.Common
{
    internal class CommonBrowserAdapter : ICefBrowserHost, IDisposable
    {
        private readonly object _eventsEmitter;
        private readonly string _name;
        private readonly ILogger _logger;
        private readonly IControl _control;
        private readonly IPopup _popup;

        private bool _browserCreated;

        private string _startUrl;
        private string _title;
        private string _tooltip;

        private CefBrowser _browser;
        private CefBrowserHost _browserHost;
        private CommonCefClient _cefClient;
        private JavascriptExecutionEngine _javascriptExecutionEngine;
        private NativeObjectMethodDispatcher _objectMethodDispatcher;

        private Func<CefRectangle> getViewRectOverride;

        private readonly NativeObjectRegistry _objectRegistry = new NativeObjectRegistry();

        public CommonBrowserAdapter(object eventsEmitter, string name, IControl control, IPopup popup, ILogger logger)
        {
            _eventsEmitter = eventsEmitter;
            _name = name;
            _control = control;
            _popup = popup;
            _logger = logger;

            _startUrl = "about:blank";

            control.ScreenInfoChanged += HandleScreenInfoChanged;
            control.VisibilityChanged += HandleVisibilityChanged;
        }

        ~CommonBrowserAdapter()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
        }

        public void Dispose(bool disposing)
        {
            var browserHost = _browserHost;
            if (browserHost != null)
            {
                if (disposing)
                {
                    browserHost.CloseBrowser();
                }
                browserHost.Dispose();
                _browserHost = null;
            }

            var browser = _browser;
            if (browser != null)
            {
                browser.Dispose();
                _browser = null;
            }

            if (disposing)
            {
                BuiltInRenderHandler?.Dispose();
                PopupRenderHandler?.Dispose();
                GC.SuppressFinalize(this);
            }
        }

        public event LoadStartEventHandler LoadStart;
        public event LoadEndEventHandler LoadEnd;
        public event LoadingStateChangeEventHandler LoadingStateChange;
        public event LoadErrorEventHandler LoadError;

        public event Action Initialized;
        public event AddressChangedEventHandler AddressChanged;
        public event TitleChangedEventHandler TitleChanged;
        public event ConsoleMessageEventHandler ConsoleMessage;
        public event StatusMessageEventHandler StatusMessage;

        public event JavascriptContextLifetimeEventHandler JavascriptContextCreated;
        public event JavascriptContextLifetimeEventHandler JavascriptContextReleased;
        public event JavascriptUncaughtExceptionEventHandler JavascriptUncaughtException;

        public event AsyncUnhandledExceptionEventHandler UnhandledException;

        public string Address { get => _browser?.GetMainFrame().Url ?? _startUrl; set => NavigateTo(value); }

        public bool AllowsTransparency { get; set; } = false;

        #region Cef Handlers

        public ContextMenuHandler ContextMenuHandler { get; set; }
        public DialogHandler DialogHandler { get; set; }
        public DownloadHandler DownloadHandler { get; set; }
        public DragHandler DragHandler { get; set; }
        public FindHandler FindHandler { get; set; }
        public FocusHandler FocusHandler { get; set; }
        public KeyboardHandler KeyboardHandler { get; set; }
        public RequestHandler RequestHandler { get; set; }
        public LifeSpanHandler LifeSpanHandler { get; set; }
        public DisplayHandler DisplayHandler { get; set; }
        public RenderHandler RenderHandler { get; set; }
        public JSDialogHandler JSDialogHandler { get; set; }

        #endregion

        public BuiltInRenderHandler BuiltInRenderHandler => _control.RenderHandler;

        public BuiltInRenderHandler PopupRenderHandler => _popup.RenderHandler;

        public bool IsInitialized => _browser != null;

        public bool IsLoading => _browser?.IsLoading ?? false;

        public string Title => _title;

        public double ZoomLevel
        {
            get => _browserHost?.GetZoomLevel() ?? 0;
            set => _browserHost.SetZoomLevel(value);
        }

        public CefBrowser Browser => _browser;

        private void NavigateTo(string url)
        {
            // Remove leading whitespace from the URL
            url = url.TrimStart();

            if (_browser != null)
                _browser.GetMainFrame().LoadUrl(url);
            else
                _startUrl = url;
        }

        public void LoadString(string content, string url)
        {
            // Remove leading whitespace from the URL
            url = url.TrimStart();

            if (_browser != null)
                _browser.GetMainFrame().LoadString(content, url);
        }

        public bool CanGoBack()
        {
            if (_browser != null)
                return _browser.CanGoBack;
            else
                return false;
        }

        public void GoBack()
        {
            if (_browser != null)
                _browser.GoBack();
        }

        public bool CanGoForward()
        {
            if (_browser != null)
                return _browser.CanGoForward;
            else
                return false;
        }

        public void GoForward()
        {
            if (_browser != null)
                _browser.GoForward();
        }

        public void Reload(bool ignoreCache)
        {
            if (_browser != null)
            {
                if (ignoreCache)
                {
                    _browser.ReloadIgnoreCache();
                }
                else
                {
                    _browser.Reload();
                }
            }
        }

        public void ExecuteJavaScript(string code, string url, int line)
        {
            if (_browser != null)
                _browser.GetMainFrame().ExecuteJavaScript(code, url, line);
        }

        public Task<T> EvaluateJavaScript<T>(string code, string url, int line, string frameName = null)
        {
            if (_browser != null)
            {
                var frame = frameName != null ? _browser.GetFrame(frameName) : _browser.GetMainFrame();
                if (frame != null)
                {
                    return EvaluateJavaScript<T>(code, url, line, frame);
                }
            }

            return Task.FromResult<T>(default(T));
        }

        public Task<T> EvaluateJavaScript<T>(string code, string url, int line, CefFrame frame)
        {
            if (frame.IsValid)
            {
                return _javascriptExecutionEngine.Evaluate<T>(code, url, line, frame);
            }

            return Task.FromResult<T>(default(T));
        }

        private void HandleGotFocus()
        {
            WithErrorHandling(nameof(HandleGotFocus), () =>
            {
                if (_browserHost != null)
                {
                    _browserHost.SendFocusEvent(true);
                }
            });
        }

        private void HandleLostFocus()
        {
            WithErrorHandling(nameof(HandleLostFocus), () =>
            { 
                if (_browserHost != null)
                {
                    _browserHost.SendFocusEvent(false);
                }
            });
        }

        private void HandleMouseMove(CefMouseEvent mouseEvent)
        {
            WithErrorHandling(nameof(HandleMouseMove), () =>
            {
                if (_browserHost != null)
                {
                    _browserHost.SendMouseMoveEvent(mouseEvent, false);
                }
            });
        }

        private void HandleMouseLeave(CefMouseEvent mouseEvent)
        {
            WithErrorHandling(nameof(HandleMouseLeave), () =>
            {
                if (_browserHost != null)
                {
                    _browserHost.SendMouseMoveEvent(mouseEvent, true);
                }
            });
        }

        private void HandleMouseButtonDown(IControl control, CefMouseEvent mouseEvent, CefMouseButtonType mouseButton, int clickCount)
        {
            WithErrorHandling(nameof(HandleMouseButtonDown), () =>
            {
                control.Focus();
                if (_browserHost != null)
                {
                    SendMouseClickEvent(mouseEvent, mouseButton, false, clickCount);
                }
            });
        }

        private void HandleMouseButtonUp(CefMouseEvent mouseEvent, CefMouseButtonType mouseButton)
        {
            WithErrorHandling(nameof(HandleMouseButtonUp), () =>
            {
                if (_browserHost != null)
                {
                    SendMouseClickEvent(mouseEvent, mouseButton, true, 1);
                }
            });
        }

        private void HandleMouseWheel(CefMouseEvent mouseEvent, int deltaX, int deltaY)
        {
            WithErrorHandling(nameof(HandleMouseWheel), () =>
            {
                if (_browserHost != null)
                {
                    _browserHost.SendMouseWheelEvent(mouseEvent, deltaX, deltaY);
                }
            });
        }

        private void HandleTextInput(string text, out bool handled)
        {
            var _handled = false;

            WithErrorHandling(nameof(HandleMouseWheel), () =>
            {
                if (_browserHost != null)
                {
                    foreach (var c in text)
                    {
                        var keyEvent = new CefKeyEvent()
                        {
                            EventType = CefKeyEventType.Char,
                            WindowsKeyCode = c,
                            Character = c
                        };

                        _browserHost.SendKeyEvent(keyEvent);
                    }

                    _handled = true;
                }
            });

            handled = _handled;
        }

        private void HandleKeyPress(CefKeyEvent keyEvent, out bool handled)
        {
            WithErrorHandling(nameof(HandleKeyPress), () =>
            {
                if (_browserHost != null)
                {
                    //_logger.Debug(string.Format("KeyDown: system key {0}, key {1}", arg.SystemKey, arg.Key));
                    SendKeyPressEvent(keyEvent);
                }
            });
            handled = false;
        }

        private void HandleDragEnter(CefMouseEvent mouseEvent, CefDragData dragData, CefDragOperationsMask effects)
        {
            WithErrorHandling(nameof(HandleDragEnter), () =>
            {
                if (_browserHost != null)
                {
                    _browserHost.DragTargetDragEnter(dragData, mouseEvent, effects);
                    _browserHost.DragTargetDragOver(mouseEvent, effects);
                }
            });
        }

        private void HandleDragOver(CefMouseEvent mouseEvent, CefDragOperationsMask effects)
        {
            WithErrorHandling(nameof(HandleDragOver), () =>
            {
                if (_browserHost != null)
                {
                    _browserHost.DragTargetDragOver(mouseEvent, effects);
                }
            });

            // TODO
            //e.Effects = currentDragDropEffects;
            //e.Handled = true;
        }

        private void HandleDragLeave()
        {
            WithErrorHandling(nameof(HandleDragLeave), () =>
            {
                if (_browserHost != null)
                {
                    _browserHost.DragTargetDragLeave();
                }
            });
        }

        private void HandleDrop(CefMouseEvent mouseEvent, CefDragOperationsMask effects)
        {
            WithErrorHandling(nameof(HandleDrop), () =>
            {
                if (_browserHost != null)
                {
                    _browserHost.DragTargetDragOver(mouseEvent, effects);
                    _browserHost.DragTargetDrop(mouseEvent);
                }
            });
        }

        private void HandleVisibilityChanged(bool isVisible)
        {
            WithErrorHandling(nameof(HandleVisibilityChanged), () =>
            {
                if (_browserHost != null)
                {
                    _browserHost.WasHidden(!isVisible);
                    // workaround cef OSR bug (https://bitbucket.org/chromiumembedded/cef/issues/2483/osr-invalidate-does-not-generate-frame)
                    // we notify browser of a resize and return height+1px on next GetViewRect call
                    // then restore the original size back again
                    getViewRectOverride = () =>
                    {
                        getViewRectOverride = null;
                        _browserHost?.WasResized();
                        return new CefRectangle(0, 0, BuiltInRenderHandler?.Width ?? 1, (BuiltInRenderHandler?.Height + 1) ?? 1);
                    };
                    _browserHost.WasResized();
                }
            });
        }

        private void HandleScreenInfoChanged(float deviceScaleFactor)
        {
            WithErrorHandling(nameof(HandleScreenInfoChanged), () =>
            {
                BuiltInRenderHandler.DeviceScaleFactor = deviceScaleFactor;

                if (_browserHost != null)
                {
                    _browserHost.NotifyScreenInfoChanged();
                }
            });
        }

        public void CreateOrUpdateBrowser(int newWidth, int newHeight)
        {
            _logger.Debug("BrowserResize. Old H{0}xW{1}; New H{2}xW{3}.", RenderedWidth, RenderedHeight, newHeight, newWidth);

            if (newWidth > 0 && newHeight > 0)
            {
                if (!_browserCreated)
                {
                    // Find the window that's hosting us
                    var hParentWnd = _control.GetHostWindowHandle();
                    if (hParentWnd != null)
                    {
                        _browserCreated = true;

                        AttachEventHandlers(_control);
                        AttachEventHandlers(_popup);

                        // Create the bitmap that holds the rendered website bitmap
                        OnBrowserSizeChanged(newWidth, newHeight);

                        var windowInfo = CefWindowInfo.Create();
                        windowInfo.SetAsWindowless(hParentWnd.Value, AllowsTransparency);

                        _cefClient = new CommonCefClient(this, _logger);
                        _cefClient.Dispatcher.RegisterMessageHandler(Messages.UnhandledException.Name, OnBrowserProcessUnhandledException);

                        // This is the first time the window is being rendered, so create it.
                        CefBrowserHost.CreateBrowser(windowInfo, _cefClient, Settings, string.IsNullOrEmpty(Address) ? "about:blank" : Address);
                    }
                }
                else
                {
                    // Only update the bitmap if the size has changed
                    if (RenderedWidth != newWidth || RenderedHeight != newHeight)
                    {
                        OnBrowserSizeChanged(newWidth, newHeight);

                        // If the window has already been created, just resize it
                        if (_browserHost != null)
                        {
                            _logger.Trace("CefBrowserHost::WasResized to {0}x{1}.", newWidth, newHeight);
                            _browserHost.WasResized();
                        }
                    }
                }
            }
        }

        public void ShowDeveloperTools()
        {
            var windowInfo = CefWindowInfo.Create();
            windowInfo.SetAsPopup(_browserHost.GetWindowHandle(), "DevTools");

            _browserHost.ShowDevTools(windowInfo, _browserHost.GetClient(), new CefBrowserSettings(), new CefPoint());
        }

        public void CloseDeveloperTools()
        {
            _browserHost.CloseDevTools();
        }

        public void RegisterJavascriptObject(object targetObject, string name, JavascriptObjectMethodCallHandler methodHandler = null)
        {
            _objectRegistry.Register(targetObject, name, methodHandler);
        }

        public void UnregisterJavascriptObject(string name)
        {
            _objectRegistry.Unregister(name);
        }

        public bool IsJavascriptObjectRegistered(string name)
        {
            return _objectRegistry.Get(name) != null;
        }

        protected void AttachEventHandlers(IControl control)
        {
            control.GotFocus += HandleGotFocus;
            control.LostFocus += HandleLostFocus;

            control.MouseMoved += HandleMouseMove;
            control.MouseLeave += HandleMouseLeave;
            control.MouseButtonPressed += HandleMouseButtonDown;
            control.MouseButtonReleased += HandleMouseButtonUp;
            control.MouseWheelChanged += HandleMouseWheel;

            control.KeyDown += HandleKeyPress;
            control.KeyUp += HandleKeyPress;

            control.TextInput += HandleTextInput;

            control.DragEnter += HandleDragEnter;
            control.DragOver += HandleDragOver;
            control.DragLeave += HandleDragLeave;
            control.Drop += HandleDrop;
        }

        protected int RenderedWidth => BuiltInRenderHandler.Width;

        protected int RenderedHeight => BuiltInRenderHandler.Height;

        public bool IsJavascriptEngineInitialized => _javascriptExecutionEngine.IsMainFrameContextInitialized;

        public CefBrowserSettings Settings { get; } = new CefBrowserSettings();

        protected void OnBrowserSizeChanged(int newWidth, int newHeight)
        {
            BuiltInRenderHandler?.Resize(newWidth, newHeight);
        }

        #region ICefBrowserHost

        void ICefBrowserHost.GetViewRect(out CefRectangle rect)
        {
            rect = GetViewRect();
        }

        protected virtual CefRectangle GetViewRect()
        {
            // The simulated screen and view rectangle are the same. This is necessary
            // for popup menus to be located and sized inside the view.
            return getViewRectOverride?.Invoke() ?? new CefRectangle(0, 0, RenderedWidth, RenderedHeight);
        }

        void ICefBrowserHost.GetScreenPoint(int viewX, int viewY, ref int screenX, ref int screenY)
        {
            GetScreenPoint(viewX, viewY, ref screenX, ref screenY);
        }

        protected void GetScreenPoint(int viewX, int viewY, ref int screenX, ref int screenY)
        {
            var point = new Point(0, 0);
            WithErrorHandling(nameof(GetScreenPoint), () =>
            {
                point = _control.PointToScreen(new Point(viewX, viewY));
            });
            screenX = point.X;
            screenY = point.Y;
        }

        void ICefBrowserHost.GetScreenInfo(CefScreenInfo screenInfo)
        {
            screenInfo.DeviceScaleFactor = BuiltInRenderHandler.DeviceScaleFactor;
        }

        void ICefBrowserHost.HandlePopupShow(bool show)
        {
            WithErrorHandling(nameof(ICefBrowserHost.HandlePopupShow), () =>
            {
                if (show)
                {
                    _popup.Open();
                }
                else
                {
                    _popup.Close();
                }
            });
        }

        void ICefBrowserHost.HandlePopupSizeChange(CefRectangle rect)
        {
            WithErrorHandling(nameof(ICefBrowserHost.HandlePopupSizeChange), () =>
            {
                _popup.RenderHandler.Resize(rect.Width, rect.Height);
                _popup.MoveAndResize(rect.X, rect.Y, rect.Width, rect.Height);
            });
        }

        void ICefBrowserHost.HandleCursorChange(IntPtr cursorHandle)
        {
            WithErrorHandling((nameof(ICefBrowserHost.HandleCursorChange)), () =>
            {
                _control.SetCursor(cursorHandle);
            });
        }

        void ICefBrowserHost.HandleBrowserCreated(CefBrowser browser)
        {
            WithErrorHandling((nameof(ICefBrowserHost.HandleBrowserDestroyed)), () =>
            {
                OnBrowserCreated(browser);
            });
        }

        void ICefBrowserHost.HandleBrowserDestroyed(CefBrowser browser)
        {
            WithErrorHandling((nameof(ICefBrowserHost.HandleBrowserDestroyed)), () =>
            {
                _objectMethodDispatcher?.Dispose();
                _objectMethodDispatcher = null;
            });
        }

        protected virtual void OnBrowserCreated(CefBrowser browser)
        {
            int width = 0, height = 0;

            if (_browser != null)
            {
                // Make sure we don't initialize ourselves more than once. That seems to break things.
                return;
            }

            WithErrorHandling((nameof(OnBrowserCreated)), () =>
            {
                _javascriptExecutionEngine = new JavascriptExecutionEngine(_cefClient.Dispatcher);
                _javascriptExecutionEngine.ContextCreated += OnJavascriptExecutionEngineContextCreated;
                _javascriptExecutionEngine.ContextReleased += OnJavascriptExecutionEngineContextReleased;
                _javascriptExecutionEngine.UncaughtException += OnJavascriptExecutionEngineUncaughtException;

                _objectRegistry.SetBrowser(browser);
                _objectMethodDispatcher = new NativeObjectMethodDispatcher(_cefClient.Dispatcher, _objectRegistry);

                _browser = browser;
                _browserHost = browser.GetHost();
                _startUrl = null;

                width = RenderedWidth;
                height = RenderedHeight;

                if (width > 0 && height > 0)
                {
                    _browserHost.WasResized();
                }

                Initialized?.Invoke();
            });
        }

        private void OnJavascriptExecutionEngineContextCreated(CefFrame frame)
        {
            JavascriptContextCreated?.Invoke(_eventsEmitter, new JavascriptContextLifetimeEventArgs(frame));
        }

        private void OnJavascriptExecutionEngineContextReleased(CefFrame frame)
        {
            JavascriptContextReleased?.Invoke(_eventsEmitter, new JavascriptContextLifetimeEventArgs(frame));
        }

        private void OnJavascriptExecutionEngineUncaughtException(JavascriptUncaughtExceptionEventArgs args)
        {
            JavascriptUncaughtException?.Invoke(_eventsEmitter, args);
        }

        bool ICefBrowserHost.HandleTooltip(CefBrowser browser, string text)
        {
            WithErrorHandling((nameof(ICefBrowserHost.HandleTooltip)), () =>
            {
                if (_tooltip == text)
                {
                    return;
                }

                _tooltip = text;
                _control.SetTooltip(text);
            });

            return true;
        }

        void ICefBrowserHost.HandleAddressChange(CefBrowser browser, CefFrame frame, string url)
        {
            AddressChanged?.Invoke(_eventsEmitter, url);
        }

        void ICefBrowserHost.HandleTitleChange(CefBrowser browser, string title)
        {
            _title = title;
            TitleChanged?.Invoke(_eventsEmitter, title);
        }

        void ICefBrowserHost.HandleStatusMessage(CefBrowser browser, string value)
        {
            StatusMessage?.Invoke(_eventsEmitter, value);
        }

        bool ICefBrowserHost.HandleConsoleMessage(CefBrowser browser, CefLogSeverity level, string message, string source, int line)
        {
            var handler = ConsoleMessage;
            if (handler != null)
            {
                var args = new ConsoleMessageEventArgs(level, message, source, line);
                ConsoleMessage?.Invoke(_eventsEmitter, args);
                return !args.OutputToConsole;
            }
            return false;
        }

        void ICefBrowserHost.HandleLoadStart(CefBrowser browser, CefFrame frame, CefTransitionType transitionType)
        {
            LoadStart?.Invoke(_eventsEmitter, new LoadStartEventArgs(frame));
        }

        void ICefBrowserHost.HandleLoadEnd(CefBrowser browser, CefFrame frame, int httpStatusCode)
        {
            LoadEnd?.Invoke(_eventsEmitter, new LoadEndEventArgs(frame, httpStatusCode));
        }

        void ICefBrowserHost.HandleLoadError(CefBrowser browser, CefFrame frame, CefErrorCode errorCode, string errorText, string failedUrl)
        {
            LoadError?.Invoke(_eventsEmitter, new LoadErrorEventArgs(frame, errorCode, errorText, failedUrl));
        }

        void ICefBrowserHost.HandleLoadingStateChange(CefBrowser browser, bool isLoading, bool canGoBack, bool canGoForward)
        {
            LoadingStateChange?.Invoke(_eventsEmitter, new LoadingStateChangeEventArgs(isLoading, canGoBack, canGoForward));
        }

        void ICefBrowserHost.HandleViewPaint(IntPtr buffer, int width, int height, CefRectangle[] dirtyRects, bool isPopup)
        {
            BuiltInRenderHandler renderHandler;
            if (isPopup)
            {
                renderHandler = PopupRenderHandler;
            }
            else
            {
                renderHandler = BuiltInRenderHandler;
            }

            const string ScopeName = nameof(ICefBrowserHost.HandleViewPaint);

            WithErrorHandling(ScopeName, () =>
            {
                renderHandler?.Paint(buffer, width, height, dirtyRects)
                              .ContinueWith(t => HandleException(ScopeName, t.Exception), TaskContinuationOptions.OnlyOnFaulted);
            });
        }

        #endregion

        private void SendMouseClickEvent(CefMouseEvent mouseEvent, CefMouseButtonType mouseButton, bool isMouseUp, int clickCount)
        {
            _browserHost.SendMouseClickEvent(mouseEvent, mouseButton, isMouseUp, clickCount);
        }

        private void SendKeyPressEvent(CefKeyEvent keyEvent)
        {
            _browserHost.SendKeyEvent(keyEvent);
        }

        void ICefBrowserHost.HandleException(Exception exception)
        {
            HandleException("Unknown", exception);
        }

        protected void WithErrorHandling(string scopeName, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                HandleException(scopeName, ex);
            }
        }

        private void HandleException(string scopeName, Exception exception)
        {
            _logger.ErrorException($"{_name} : Caught exception in {scopeName}()", exception);
            UnhandledException?.Invoke(_eventsEmitter, new AsyncUnhandledExceptionEventArgs(exception));
        }

        private void OnBrowserProcessUnhandledException(MessageReceivedEventArgs e)
        {
            var exceptionDetails = Messages.UnhandledException.FromCefMessage(e.Message);
            _logger.Error("Browser process unhandled exception", "Type: " + exceptionDetails.ExceptionType, "Message: " + exceptionDetails.Message, "StackTrace: " +  exceptionDetails.StackTrace);

            UnhandledException?.Invoke(
                _eventsEmitter, 
                new AsyncUnhandledExceptionEventArgs(new RenderProcessUnhandledException(exceptionDetails.ExceptionType, exceptionDetails.Message, exceptionDetails.StackTrace)));
        }
    }
}
