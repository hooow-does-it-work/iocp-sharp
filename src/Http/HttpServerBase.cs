﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Net;
using IocpSharp.Http.Responsers;
using IocpSharp.Http.Streams;
using System.IO.Compression;
using IocpSharp.Server;
using IocpSharp.Http.Utils;
using IocpSharp.WebSocket;

namespace IocpSharp.Http
{
    //我们独立出一个基类来，以后新的服务继承本类就好
    public class HttpServerBase : TcpIocpServer
    {
        private static int MaxRequestPerConnection = 20;

        private string _webRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web"));
        private static string _uplaodTempDir = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "uploads"));

        public string WebRoot { get => _webRoot; set => _webRoot = value; }
        public static string UplaodTempDir { get => _uplaodTempDir; set => _uplaodTempDir = value; }

        //后面的代码可能会越来越复杂，我们做个简单的路由功能
        //可以开发功能更强大的路由
        private Dictionary<string, Func<HttpRequest, Stream, bool>> _routes = new Dictionary<string, Func<HttpRequest, Stream, bool>>();

        public HttpServerBase() : base()
        {
        }
        protected override void Start()
        {
            if (!Directory.Exists(_webRoot)) throw new Exception($"网站根目录不存在，请手动创建：{_webRoot}");

            base.Start();
        }

        public void RegisterRoute(string path, Func<HttpRequest, Stream, bool> route)
        {
            _routes[path] = route;
        }

        protected override void NewClient(Socket client)
        {
            HttpStream stream = new HttpStream(new BufferedNetworkStream(client, true), false);
            EndPoint localEndPoint = client.LocalEndPoint;
            EndPoint remoteEndPoint = client.RemoteEndPoint;
            //设置每个链接能处理的请求数
            int processedRequest = 0;
            HttpRequest request = null;
            while (processedRequest < MaxRequestPerConnection)
            {
                try
                {
                    //捕获一个HttpRequest
                    request = request == null ? stream.Capture<HttpRequest>() : request.Next();
                    if (request == null) break;

                    ///如果是WebSocket，调用相应的处理方法
                    if (request.IsWebSocket)
                    {
                        if (!OnWebSocketInternal(request, stream, localEndPoint, remoteEndPoint))
                        {
                            //WebSocket处理异常，关闭基础流
                            stream.Close();
                        }
                        break;
                    }

                    //尝试查找路由，不存在的话使用NotFound路由
                    if (!_routes.TryGetValue(request.Path, out Func<HttpRequest, Stream, bool> handler))
                    {
                        //未匹配到路由，统一当文件资源处理
                        handler = OnResource;
                    }

                    //如果处理程序返回false，那么我们退出循环，关掉连接。
                    if (!handler(request, stream)) break;

                    //释放掉当前请求，准备下一次请求
                    processedRequest++;
                }
                catch (HttpHeaderException e)
                {
                    if (e.Error == HttpHeaderError.ConnectionLost) break;

                    //客户端发送的请求异常
                    OnBadRequest(stream, $"请求异常：{e.Error}");
                    break;

                }
                catch (Exception e)
                {
                    //其他异常
                    OnServerError(stream, $"请求异常：{e}");
                    break;
                }
            }
            request?.Dispose();
            stream.Close();
        }

        /// <summary>
        /// 响应404错误
        /// </summary>
        /// <param name="request"></param>
        /// <param name="stream"></param>
        /// <returns></returns>
        protected virtual bool OnNotFound(HttpRequest request, Stream stream)
        {
            HttpResponser responser = new ChunkedResponser(404);
            responser.KeepAlive = false;
            responser.ContentType = "text/html; charset=utf-8";
            responser.Write(stream, $"请求的资源'{request.Path}'不存在。");
            responser.End(stream);
            return false;
        }


        /// <summary>
        /// 请求异常
        /// </summary>
        /// <param name="request"></param>
        /// <param name="stream"></param>
        /// <param name="message"></param>
        protected virtual void OnBadRequest(Stream stream, string message)
        {
            HttpResponser responser = new ChunkedResponser(400);
            responser.KeepAlive = false;
            responser.ContentType = "text/html; charset=utf-8";
            responser.Write(stream, message);
            responser.End(stream);
        }

        /// <summary>
        /// 服务器异常
        /// </summary>
        /// <param name="request"></param>
        /// <param name="stream"></param>
        /// <param name="message"></param>
        protected virtual void OnServerError(Stream stream, string message)
        {
            HttpResponser responser = new ChunkedResponser(500);
            responser.KeepAlive = false;
            responser.ContentType = "text/html; charset=utf-8";
            responser.Write(stream, message);
            responser.End(stream);
        }

        /// <summary>
        /// 发送服务器资源，这里简单处理下。
        /// 必要的情况下可以作缓存处理
        /// </summary>
        /// <param name="request"></param>
        /// <param name="stream"></param>
        protected virtual bool OnResource(HttpRequest request, Stream stream)
        {
            string path = request.Path;

            ///处理下非安全的路径
            if (path.IndexOf("..") >= 0 || !path.StartsWith("/"))
            {
                OnBadRequest(stream, "不安全的路径访问");
                return false;
            }


            string filePath = Path.GetFullPath(Path.Combine(_webRoot, "." + path));

            if(filePath.IndexOf(".") == -1) return OnNotFound(request, stream);

            FileInfo fileInfo = new FileInfo(filePath);
            string mimeType = MimeTypes.GetMimeType(fileInfo.Extension);

            if (string.IsNullOrEmpty(mimeType))
            {
                OnBadRequest(stream, "不支持的文件类型");
                return false;
            }

            if (!fileInfo.Exists)
            {
                return OnNotFound(request, stream);
            }

            HttpResponser responser = new HttpResponser();

            //拿到的MIME输出给客户端
            responser.ContentType = mimeType;
            responser.ContentLength = fileInfo.Length;

            using (Stream output = responser.OpenWrite(stream))
            {
                using(Stream input = fileInfo.OpenRead())
                {
                    input.CopyTo(stream);
                }
            }
            return true;
        }

        /// <summary>
        /// 处理WebSocket
        /// </summary>
        /// <param name="request"></param>
        /// <param name="stream"></param>
        /// <returns></returns>
        private bool OnWebSocketInternal(HttpRequest request, Stream stream, EndPoint localEndPoint, EndPoint remoteEndPoint)
        {
            string webSocketKey = request.Headers["Sec-WebSocket-Key"];
            if(string.IsNullOrEmpty(webSocketKey))
            {
                OnBadRequest(stream, "header 'Sec-WebSocket-Key' error");
                return false;
            }

            //获取客户端发送来的Sec-WebSocket-Key字节数组
            byte[] keyBytes = Encoding.ASCII.GetBytes(webSocketKey);

            //拼接上WebSocket的Salt，固定值：258EAFA5-E914-47DA-95CA-C5AB0DC85B11
            keyBytes = keyBytes.Concat(ProtocolUtils.Salt).ToArray();

            //计算HASH值，作为响应给客户端的Sec-WebSocket-Accept
            string secWebSocketAcceptKey = ProtocolUtils.SHA1(keyBytes);

            //响应101状态码给客户端
            HttpResponser responser = new HttpResponser(101);
            responser["Upgrade"] = "websocket";
            responser["Connection"] = "Upgrade";

            //设置Sec-WebSocket-Accept头
            responser["Sec-WebSocket-Accept"] = secWebSocketAcceptKey;

            //发送响应
            responser.WriteHeader(stream);


            //开始WebSocket消息的接收和发送
            Messager messager = GetMessager(request, stream, localEndPoint, remoteEndPoint);
            if (messager != null) messager.Accept();
            return true;
        }

        /// <summary>
        /// WebSocket消息处理程序
        /// </summary>
        /// <param name="request"></param>
        /// <param name="stream"></param>
        protected virtual Messager GetMessager(HttpRequest request, Stream stream, EndPoint localEndPoint, EndPoint remoteEndPoint) {
            stream.Close();
            return null;
        }
    }
}
