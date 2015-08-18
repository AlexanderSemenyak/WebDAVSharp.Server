﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using WebDAVSharp.Server.Adapters;
using WebDAVSharp.Server.Adapters.AuthenticationTypes;
using WebDAVSharp.Server.Exceptions;
using WebDAVSharp.Server.MethodHandlers;
using WebDAVSharp.Server.Stores;
using log4net;
using WebDAVSharp.Server.Stores.Locks;
using System.Xml;
using WebDAVSharp.Server.Utilities;
using System.Diagnostics;

namespace WebDAVSharp.Server
{
    /// <summary>
    /// This class implements the core WebDAV server.
    /// </summary>
    public class WebDavServer : WebDavDisposableBase
    {

        #region Variables
        /// <summary>
        /// The HTTP user
        /// </summary>
        public const string HttpUser = "HTTP.User";

        private readonly IHttpListener _listener;
        private readonly bool _ownsListener;
        private readonly IWebDavStore _store;
        private readonly Dictionary<string, IWebDavMethodHandler> _methodHandlers;
        internal readonly static ILog _log = LogManager.GetLogger("WebDavServer");
        private readonly object _threadLock = new object();
        private ManualResetEvent _stopEvent;

        private Thread _thread;

        #endregion

        #region Properties

        /// <summary>
        /// Allow users to have Indefinite Locks
        /// </summary>
        public bool AllowInfiniteCheckouts
        {
            get
            {
                return WebDavStoreItemLock.AllowInfiniteCheckouts;
            }
            set
            {
                WebDavStoreItemLock.AllowInfiniteCheckouts = value;
            }
        }

        /// <summary>
        /// The maximum number of seconds a person can check an item out for.
        /// </summary>
        public long MaxCheckOutSeconds
        {
            get
            {
                return WebDavStoreItemLock.MaxCheckOutSeconds;
            }
            set
            {
                WebDavStoreItemLock.MaxCheckOutSeconds = value;
            }
        }
        /// <summary>
        /// Logging Interface
        /// </summary>
        public static ILog Log
        {
            get
            {
                return _log;
            }
        }

        /// <summary>
        /// Gets the <see cref="IWebDavStore" /> this <see cref="WebDavServer" /> is hosting.
        /// </summary>
        /// <value>
        /// The store.
        /// </value>
        public IWebDavStore Store
        {
            get
            {
                return _store;
            }
        }

        /// <summary>
        /// Gets the 
        /// <see cref="IHttpListener" /> that this 
        /// <see cref="WebDavServer" /> uses for
        /// the web server portion.
        /// </summary>
        /// <value>
        /// The listener.
        /// </value>
        internal protected IHttpListener Listener
        {
            get
            {
                return _listener;
            }
        }

        #endregion

        #region Constructor

        /// <summary>
        /// 
        /// </summary>
        /// <param name="store"></param>
        /// <param name="authtype"></param>
        /// <param name="methodHandlers"></param>
        public WebDavServer(IWebDavStore store, AuthType authtype, IEnumerable<IWebDavMethodHandler> methodHandlers = null)
        {
            _ownsListener = true;
            switch (authtype)
            {
                case AuthType.Basic:
                    _listener = new HttpListenerBasicAdapter();
                    break;
                case AuthType.Negotiate:
                    _listener = new HttpListenerNegotiateAdapter();
                    break;
                case AuthType.Anonymous:
                    _listener = new HttpListenerAnyonymousAdapter();
                    break;

            }
            methodHandlers = methodHandlers ?? WebDavMethodHandlers.BuiltIn;

            IWebDavMethodHandler[] webDavMethodHandlers = methodHandlers as IWebDavMethodHandler[] ?? methodHandlers.ToArray();

            if (!webDavMethodHandlers.Any())
                throw new ArgumentException("The methodHandlers collection is empty", "methodHandlers");
            if (webDavMethodHandlers.Any(methodHandler => methodHandler == null))
                throw new ArgumentException("The methodHandlers collection contains a null-reference", "methodHandlers");

            //_negotiateListener = listener;
            _store = store;
            var handlersWithNames =
                from methodHandler in webDavMethodHandlers
                from name in methodHandler.Names
                select new
                {
                    name,
                    methodHandler
                };
            _methodHandlers = handlersWithNames.ToDictionary(v => v.name, v => v.methodHandler);

        }

        /// <summary>
        /// This constructor uses a Negotiate Listener if one isn't passed.
        /// 
        /// Initializes a new instance of the <see cref="WebDavServer" /> class.
        /// </summary>
        /// <param name="store">The 
        /// <see cref="IWebDavStore" /> store object that will provide
        /// collections and documents for this 
        /// <see cref="WebDavServer" />.</param>
        /// <param name="listener">The 
        /// <see cref="IHttpListener" /> object that will handle the web server portion of
        /// the WebDAV server; or 
        /// <c>null</c> to use a fresh one.</param>
        /// <param name="methodHandlers">A collection of HTTP method handlers to use by this 
        /// <see cref="WebDavServer" />;
        /// or 
        /// <c>null</c> to use the built-in method handlers.</param>
        /// <exception cref="System.ArgumentNullException"><para>
        ///   <paramref name="listener" /> is <c>null</c>.</para>
        /// <para>- or -</para>
        /// <para>
        ///   <paramref name="store" /> is <c>null</c>.</para></exception>
        /// <exception cref="System.ArgumentException"><para>
        ///   <paramref name="methodHandlers" /> is empty.</para>
        /// <para>- or -</para>
        /// <para>
        ///   <paramref name="methodHandlers" /> contains a <c>null</c>-reference.</para></exception>
        public WebDavServer(IWebDavStore store, IHttpListener listener = null, IEnumerable<IWebDavMethodHandler> methodHandlers = null)
        {
            if (store == null)
                throw new ArgumentNullException("store");
            if (listener == null)
            {
                listener = new HttpListenerNegotiateAdapter();
                _ownsListener = true;
            }
            methodHandlers = methodHandlers ?? WebDavMethodHandlers.BuiltIn;

            IWebDavMethodHandler[] webDavMethodHandlers = methodHandlers as IWebDavMethodHandler[] ?? methodHandlers.ToArray();

            if (!webDavMethodHandlers.Any())
                throw new ArgumentException("The methodHandlers collection is empty", "methodHandlers");
            if (webDavMethodHandlers.Any(methodHandler => methodHandler == null))
                throw new ArgumentException("The methodHandlers collection contains a null-reference", "methodHandlers");

            _listener = listener;
            _store = store;
            var handlersWithNames =
                from methodHandler in webDavMethodHandlers
                from name in methodHandler.Names
                select new
                {
                    name,
                    methodHandler
                };
            _methodHandlers = handlersWithNames.ToDictionary(v => v.name, v => v.methodHandler);
        }

        #endregion

        #region Functions

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            lock (_threadLock)
            {
                if (_thread != null)
                    Stop();
            }

            if (_ownsListener)
                _listener.Dispose();
        }

        /// <summary>
        /// Starts this 
        /// <see cref="WebDavServer" /> and returns once it has
        /// been started successfully.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">This WebDAVServer instance is already running, call to Start is invalid at this point</exception>
        /// <exception cref="ObjectDisposedException">This <see cref="WebDavServer" /> instance has been disposed of.</exception>
        /// <exception cref="InvalidOperationException">The server is already running.</exception>
        public void Start(String url)
        {
            Listener.Prefixes.Add(url);
            EnsureNotDisposed();
            lock (_threadLock)
            {
                if (_thread != null)
                {
                    throw new InvalidOperationException(
                        "This WebDAVServer instance is already running, call to Start is invalid at this point");
                }

                _stopEvent = new ManualResetEvent(false);

                _thread = new Thread(BackgroundThreadMethod)
                {
                    Name = "WebDAVServer.Thread",
                    Priority = ThreadPriority.Highest
                };
                _thread.Start();
            }
        }

        /// <summary>
        /// Starts this 
        /// <see cref="WebDavServer" /> and returns once it has
        /// been stopped successfully.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">This WebDAVServer instance is not running, call to Stop is invalid at this point</exception>
        /// <exception cref="ObjectDisposedException">This <see cref="WebDavServer" /> instance has been disposed of.</exception>
        /// <exception cref="InvalidOperationException">The server is not running.</exception>
        public void Stop()
        {
            EnsureNotDisposed();
            lock (_threadLock)
            {
                if (_thread == null)
                {
                    throw new InvalidOperationException(
                        "This WebDAVServer instance is not running, call to Stop is invalid at this point");
                }

                _stopEvent.Set();
                _thread.Join();

                _stopEvent.Close();
                _stopEvent = null;
                _thread = null;
            }
        }

        /// <summary>
        /// The background thread method.
        /// </summary>
        private void BackgroundThreadMethod()
        {
            _log.Info("WebDAVServer background thread has started");
            try
            {
                _listener.Start();
                Stopwatch sw = new Stopwatch();
                sw.Start();
                while (true)
                {
                    _log.DebugFormat("BackgroundThreadMethod poll ms: {0}", sw.ElapsedMilliseconds);
                    if (_stopEvent.WaitOne(0))
                        return;

                    sw.Reset();
                    IHttpListenerContext context = Listener.GetContext(_stopEvent);
                    if (context == null)
                    {
                        _log.Debug("Exiting thread");
                        return;
                    }
                    _log.DebugFormat("Queued Context request: {0}", context.Request.HttpMethod);

                    ThreadPool.QueueUserWorkItem(ProcessRequest, context);
                }
            }
            catch (Exception ex)
            {
                //This error occours if we are not able to queue a request, the whole webdav server
                //is terminating.
                _log.Error(String.Format("Web dav ended unexpectedly with error {0}", ex.Message) , ex);
            }
            finally
            {
                _listener.Stop();
                _log.Info("WebDAVServer background thread has terminated");
            }
        }

        /// <summary>
        /// Called before actual processing is done, useful to do some preliminary 
        /// check or whatever the real implementation need to do.
        /// </summary>
        /// <param name="context"></param>
        protected virtual void OnProcessRequestStarted(IHttpListenerContext context)
        {

        }

        /// <summary>
        /// Called after everything was processed, it can be used for doing specific
        /// cleanup for the real implementation.
        /// </summary>
        /// <param name="context"></param>
        protected virtual void OnProcessRequestCompleted(IHttpListenerContext context)
        {

        }

        /// <summary>
        /// Processes the request.
        /// </summary>
        /// <param name="state">The state.</param>
        /// <exception cref="WebDAVSharp.Server.Exceptions.WebDavMethodNotAllowedException">If the method to process is not allowed</exception>
        /// <exception cref="WebDAVSharp.Server.Exceptions.WebDavUnauthorizedException">If the user is unauthorized or has no access</exception>
        /// <exception cref="WebDAVSharp.Server.Exceptions.WebDavNotFoundException">If the item was not found</exception>
        /// <exception cref="WebDAVSharp.Server.Exceptions.WebDavNotImplementedException">If a method is not yet implemented</exception>
        /// <exception cref="WebDAVSharp.Server.Exceptions.WebDavInternalServerException">If the server had an internal problem</exception>
        private void ProcessRequest(object state)
        {
            using (WebDavMetrics.GetMetricCallContext())
            {
                IHttpListenerContext context = (IHttpListenerContext)state;
                OnProcessRequestStarted(context);
                Thread.SetData(Thread.GetNamedDataSlot(HttpUser), Listener.GetIdentity(context));

                String callInfo = String.Format("{0} : {1} : {2}", context.Request.HttpMethod, context.Request.RemoteEndPoint, context.Request.Url);
                //_log.DebugFormat("CALL START: {0}", callInfo);
                log4net.ThreadContext.Properties["webdav-request"] = callInfo;
                XmlDocument request = null;
                XmlDocument response = null;
                StringBuilder requestHader = new StringBuilder();
                if (_log.IsDebugEnabled)
                {
                    foreach (String header in context.Request.Headers)
                    {
                        requestHader.AppendFormat("{0}: {1}\r\n", header, context.Request.Headers[header]);
                    }
                }

                try
                {
                    try
                    {
                        string method = context.Request.HttpMethod;
                        IWebDavMethodHandler methodHandler;
                        if (!_methodHandlers.TryGetValue(method, out methodHandler))
                            throw new WebDavMethodNotAllowedException(string.Format(CultureInfo.InvariantCulture, "%s ({0})", context.Request.HttpMethod));

                        context.Response.AppendHeader("DAV", "1,2,1#extend");
                        methodHandler.ProcessRequest(this, context, Store, out request, out response);

                        if (_log.IsDebugEnabled)
                        {
                            _log.DebugFormat("WEB-DAV-CALL-ENDED: {0}\r\nREQUEST HEADER\r\n{1}\r\nRESPONSE HEADER\r\n{2}\r\nrequest:{3}\r\nresponse{4}",
                                callInfo,
                                requestHader,
                                context.Response.DumpHeaders(),
                                request.Beautify(),
                                response.Beautify());
                        }
                    }
                    catch (WebDavException)
                    {
                        throw;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw new WebDavUnauthorizedException();
                    }
                    catch (FileNotFoundException ex)
                    {
                        _log.Warn("(FAILED) WEB-DAV-CALL-ENDED:" + callInfo + ": " + ex.Message, ex);
                        throw new WebDavNotFoundException("FileNotFound", innerException: ex);
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        _log.Warn("(FAILED) WEB-DAV-CALL-ENDED:" + callInfo + ": " + ex.Message, ex);
                        throw new WebDavNotFoundException("DirectoryNotFound", innerException: ex);
                    }
                    catch (NotImplementedException ex)
                    {
                        _log.Warn("(FAILED) WEB-DAV-CALL-ENDED:" + callInfo + ": " + ex.Message, ex);
                        throw new WebDavNotImplementedException(innerException: ex);
                    }
                    catch (Exception ex)
                    {
                        _log.Error("(FAILED) WEB-DAV-CALL-ENDED:" + callInfo + ": " + ex.Message, ex);
                        throw new WebDavInternalServerException(innerException: ex);
                    }
                }
                catch (WebDavException ex)
                {
                    if (ex is WebDavNotFoundException)
                    {
                        //not found exception is quite common, Windows explorer often ask for files
                        //that are not there 
                        _log.Debug(String.Format("WEB-DAV-CALL-ENDED: {0}\r\nHeader:{1}:\r\nrequest:{2}\r\nresponse{3}",
                            callInfo, requestHader, request.Beautify(), response.Beautify()), ex);
                    }
                    else
                    {
                        _log.Warn(String.Format("WEB-DAV-CALL-ENDED: {0}\r\nHeader:{1}:\r\nrequest:{2}\r\nresponse{3}",
                            callInfo, requestHader, request.Beautify(), response.Beautify()), ex);
                    }

                    SendResponseForException(context, ex);
                }
                finally
                {
                    log4net.ThreadContext.Properties["webdav-request"] = null;
                    OnProcessRequestCompleted(context);
                }
            }
        }

        private void SendResponseForException(IHttpListenerContext context, WebDavException ex)
        {
            try
            {
                context.Response.StatusCode = ex.StatusCode;
                context.Response.StatusDescription = ex.StatusDescription;
                if (ex.Message != context.Response.StatusDescription)
                {
                    byte[] buffer = Encoding.UTF8.GetBytes(ex.Message);
                    context.Response.ContentEncoding = Encoding.UTF8;
                    context.Response.ContentLength64 = buffer.Length;
                    context.Response.OutputStream.Write(buffer, 0, buffer.Length);
                    context.Response.OutputStream.Flush();
                }

                context.Response.Close();
            }
            catch (Exception innerEx)
            {
                _log.Error("Unable to send response for exception: " + innerEx.Message, innerEx);
            }
           
        }


        #endregion
    }
}