﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using IocpSharp.Http.Streams;

namespace IocpSharp.Http.Responsers
{
    /// <summary>
    /// HTTP错误应答器
    /// </summary>
    public class HttpErrorResponser : HttpTextResponser
    {
        public HttpErrorResponser(string message, int statusCode) : base(message, statusCode)
        {
        }
        protected override string GetAllHeaders(StringBuilder sb)
        {
            if (StatusCode >= 400 && StatusCode != 404)
            {
                KeepAlive = false;
            }
            return base.GetAllHeaders(sb);
        }
    }
}