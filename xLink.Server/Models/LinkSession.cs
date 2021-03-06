﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NewLife.Model;
using NewLife.Net;
using NewLife.Remoting;
using xLink.Entity;
using xLink.Models;

namespace xLink.Server.Models
{
    /// <summary>物联会话</summary>
    public abstract class LinkSession
    {
        #region 属性
        /// <summary>会话</summary>
        public IApiSession Session { get; set; }

        /// <summary>是否已登录</summary>
        public Boolean Logined { get; set; }

        /// <summary>网络类型</summary>
        public String NetType { get; set; }

        /// <summary>当前登录用户</summary>
        public IAuthUser Current { get; set; }

        /// <summary>在线对象</summary>
        public IOnline Online { get; private set; }

        /// <summary>名称</summary>
        public String Name { get; set; }

        /// <summary>平台</summary>
        public String Agent { get; set; }

        /// <summary>系统</summary>
        public String OS { get; set; }

        /// <summary>类型</summary>
        public String Type { get; set; }

        /// <summary>版本</summary>
        public String Version { get; set; }
        #endregion

        #region 登录注册
        /// <summary>检查登录，默认检查密码MD5散列，可继承修改</summary>
        /// <param name="user">用户名</param>
        /// <param name="pass">密码</param>
        /// <returns>返回要发给客户端的对象</returns>
        public virtual Object CheckLogin(String user, String pass)
        {
            if (user.IsNullOrEmpty()) throw Error(3, "用户名不能为空");

            var dic = ControllerContext.Current?.Parameters?.ToNullable();
            if (dic != null)
            {
                Agent = dic["Agent"] + "";
                OS = dic["OS"] + "";
                Type = dic["Type"] + "";
                Version = dic["Version"] + "";
            }
            // 登录
            Name = user;

            CheckOnline(user);

            var msg = "登录 {0}/{1}".F(user, pass);
            //WriteLog(msg);

            var ns = Session as NetSession;
            var flag = true;
            var act = "Login";
            try
            {
                // 查找并登录，找不到用户是返回空，登录失败则抛出异常
                var u = CheckUser(user, pass);
                if (u == null) throw Error(3, user + " 不存在");
                if (!u.Enable) throw Error(4, user + " 已被禁用");

                var rs = new { Name = u + "" };

                //u.SaveLogin(ns);
                SaveLogin(u);

                // 当前设备
                Current = u;

                var olt = Online;
                if (olt.UserID > 0 && olt.UserID != u.ID) SaveHistory("Logout", true, "=> " + u);
                olt.UserID = u.ID;
                olt.SaveAsync();

                // 销毁时
                //ns.OnDisposed += (s, e) =>
                //{
                //    Online.Delete();

                //    SaveHistory("Logout", true, null);
                //};

                return rs;
            }
            catch (Exception ex)
            {
                msg += " " + ex?.GetTrue()?.Message;
                flag = false;
                throw;
            }
            finally
            {
                SaveHistory(act, flag, msg);
            }
        }

        /// <summary>查找用户并登录，找不到用户是返回空，登录失败则抛出异常</summary>
        /// <param name="user"></param>
        /// <param name="pass"></param>
        /// <returns></returns>
        protected abstract IAuthUser CheckUser(String user, String pass);

        /// <summary>登录或注册完成后，保存登录信息</summary>
        /// <param name="user"></param>
        protected virtual void SaveLogin(IAuthUser user)
        {
            var u = user as IMyModel;
            u.Type = Type;
            u.Version = Version;
            if (u.NickName.IsNullOrEmpty()) u.NickName = "{0}{1}".F(Agent, user.Name);

            var dic = ControllerContext.Current?.Parameters?.ToNullable();
            if (dic != null)
            {
                NetType = dic["NetType"] + "";

                var olt = Online as IMyOnline;
                olt.LoginTime = DateTime.Now;
                olt.LoginCount++;
                // 本地地址
                olt.InternalUri = dic["ip"] + "";
                olt.NetType = NetType;
            }

            var ns = Session as NetSession;
            user.SaveLogin(ns);
        }
        #endregion

        #region 心跳历史
        /// <summary>更新在线信息，登录前、心跳时 调用</summary>
        /// <param name="name"></param>
        public virtual void CheckOnline(String name)
        {
            var ns = Session as NetSession;
            var u = Current;

            var olt = Online ?? CreateOnline(ns.ID);
            olt.Name = Name;
            olt.Type = Type;
            olt.SessionID = ns.ID;
            olt.UpdateTime = DateTime.Now;

            if (olt != Online)
            {
                olt.CreateTime = DateTime.Now;
                olt.CreateIP = ns?.Remote?.Address + "";
            }

            if (u != null)
            {
                olt.UserID = u.ID;
                if (olt.Name.IsNullOrEmpty()) olt.Name = u + "";
            }
            olt.SaveAsync();

            Online = olt;
        }

        /// <summary>创建在线</summary>
        /// <param name="sessionid"></param>
        /// <returns></returns>
        protected abstract IOnline CreateOnline(Int32 sessionid);

        /// <summary>保存令牌操作历史</summary>
        /// <param name="action"></param>
        /// <param name="success"></param>
        /// <param name="content"></param>
        public virtual void SaveHistory(String action, Boolean success, String content)
        {
            var hi = CreateHistory();
            hi.Name = Name;
            hi.Type = Type;

            var u = Current;
            var ot = Online;
            if (u != null)
            {
                if (hi.UserID == 0) hi.UserID = u.ID;
                if (hi.Name.IsNullOrEmpty()) hi.Name = u + "";
            }
            else if (ot != null)
            {
                if (hi.UserID == 0) hi.UserID = ot.UserID;
                //if (hi.Name.IsNullOrEmpty()) hi.Name = ot.Name;
            }
            //if (hi.CreateUserID == 0) hi.CreateUserID = hi.UserID;

            hi.Action = action;
            hi.Success = success;
            hi.Remark = content;
            hi.CreateTime = DateTime.Now;

            if (Session is NetSession sc) hi.CreateIP = sc.Remote + "";

            hi.SaveAsync();
        }

        /// <summary>创建历史</summary>
        /// <returns></returns>
        protected abstract IHistory CreateHistory();
        #endregion

        #region 读写
        /// <summary>写入数据，返回整个数据区</summary>
        /// <param name="id">设备</param>
        /// <param name="start"></param>
        /// <param name="data"></param>
        /// <returns></returns>
        public virtual async Task<Byte[]> Write(String id, Int32 start, params Byte[] data)
        {
            var rs = await Session.InvokeAsync<DataModel>("Write", new { id, start, data = data.ToHex() });
            return rs.Data.ToHex();
        }

        /// <summary>读取对方数据</summary>
        /// <param name="id">设备</param>
        /// <param name="start"></param>
        /// <param name="count"></param>
        /// <returns></returns>
        public virtual async Task<Byte[]> Read(String id, Int32 start, Int32 count)
        {
            var rs = await Session.InvokeAsync<DataModel>("Read", new { id, start, count });
            return rs.Data.ToHex();
        }

        public Func<String, Byte[]> GetData;
        public Action<String, Byte[]> SetData;

        /// <summary>收到写入请求</summary>
        /// <param name="id">设备</param>
        /// <param name="start"></param>
        /// <param name="data"></param>
        [Api("Write")]
        protected virtual DataModel OnWrite(String id, Int32 start, String data)
        {
            var buf = GetData?.Invoke(id);
            if (buf == null) throw new ApiException(405, "找不到设备！");

            var ds = data.ToHex();

            // 检查扩容
            if (start + ds.Length > buf.Length)
            {
                var buf2 = new Byte[start + ds.Length];
                buf2.Write(0, buf);
                buf = buf2;
            }
            buf.Write(start, ds);
            buf[0] = (Byte)buf.Length;

            // 保存回去
            SetData?.Invoke(id, buf);

            return new DataModel { ID = id, Start = 0, Data = buf.ToHex() };
        }

        /// <summary>收到读取请求</summary>
        /// <param name="id">设备</param>
        /// <param name="start"></param>
        /// <param name="count"></param>
        [Api("Read")]
        protected virtual DataModel OnRead(String id, Int32 start, Int32 count)
        {
            var buf = GetData?.Invoke(id);
            if (buf == null) throw new ApiException(405, "找不到设备！");

            return new DataModel { ID = id, Start = start, Data = buf.ReadBytes(start, count).ToHex() };
        }
        #endregion

        #region 异常处理
        /// <summary>抛出异常</summary>
        /// <param name="errCode"></param>
        /// <param name="msg"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        protected ApiException Error(Int32 errCode, String msg, Object result = null)
        {
            var ex = new ApiException(errCode, msg);
            if (result != null)
            {
                // 支持自定义类型
                foreach (var item in result.ToDictionary())
                {
                    ex.Data[item.Key] = item.Value;
                }
            }

            return ex;
        }
        #endregion

        #region 辅助
        private String _prefix;

        /// <summary>写日志</summary>
        /// <param name="format"></param>
        /// <param name="args"></param>
        public void WriteLog(String format, params Object[] args)
        {
            var ns = Session as NetSession;
            if (_prefix == null)
            {
                var type = GetType();
                _prefix = "{0}[{1}] ".F(type.GetDisplayName() ?? type.Name.TrimEnd("Session"), ns.ID);
                ns.LogPrefix = _prefix;
            }

            ns.WriteLog(Session["Name"] + " " + format, args);
        }
        #endregion
    }
}