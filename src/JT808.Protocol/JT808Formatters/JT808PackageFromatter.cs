﻿using System;
using System.Collections.Generic;
using System.Text;
using JT808.Protocol.Extensions;
using JT808.Protocol.Exceptions;
using JT808.Protocol.Attributes;
using System.Buffers;
using JT808.Protocol.Enums;
using JT808.Protocol.JT808Internal;

namespace JT808.Protocol.JT808Formatters
{
    /// <summary>
    /// JT808包序列化器
    /// </summary>
    public class JT808PackageFromatter : IJT808Formatter<JT808Package>
    {
        public JT808Package Deserialize(ReadOnlySpan<byte> bytes,  out int readSize)
        {
            int offset = 0;
            JT808Package jT808Package = new JT808Package();
            // 转义还原——>验证校验码——>解析消息
            // 1. 解码（转义还原）
            ReadOnlySpan<byte> buffer = JT808DeEscape(bytes);
            // 2. 验证校验码
            //  2.1. 获取校验位索引
            int checkIndex = buffer.Length - 2;
            //  2.2. 获取校验码
            jT808Package.CheckCode = buffer[checkIndex];
            //  2.3. 从消息头到校验码前一个字节
            byte checkCode = buffer.ToXor(1, checkIndex);
            //  2.4. 验证校验码
            if (!JT808GlobalConfig.Instance.SkipCRCCode)
            {
                if (jT808Package.CheckCode != checkCode)
                {
                    throw new JT808Exception(JT808ErrorCode.CheckCodeNotEqual, $"{jT808Package.CheckCode}!={checkCode}");
                }
            }
            jT808Package.Begin = buffer[offset];
            offset = offset + 1;
            // 3.初始化消息头
            try
            {
                jT808Package.Header = JT808FormatterExtensions.GetFormatter<JT808Header>().Deserialize(buffer.Slice(offset), out readSize);
            }
            catch (Exception ex)
            {
                throw new JT808Exception(JT808ErrorCode.HeaderParseError, ex);
            }
            offset = readSize;
            if (jT808Package.Header.MessageBodyProperty.DataLength != 0)
            {
                Type jT808BodiesImplType = JT808MsgIdFactory.GetBodiesImplTypeByMsgId(jT808Package.Header.MsgId);
                if (jT808BodiesImplType != null)
                {
                    if (jT808Package.Header.MessageBodyProperty.IsPackge)
                    {//4.分包消息体 从17位开始  或   未分包消息体 从13位开始
                     //消息总包数2位+包序号2位=4位
                        offset = offset + 2 + 2;
                    }
                    if (jT808Package.Header.MessageBodyProperty.DataLength != 0)
                    {
                        try
                        {
                            //5.处理消息体
                            jT808Package.Bodies = JT808FormatterResolverExtensions.JT808DynamicDeserialize(JT808FormatterExtensions.GetFormatter(jT808BodiesImplType), buffer.Slice(offset+1, jT808Package.Header.MessageBodyProperty.DataLength),  out readSize);
                        }
                        catch (Exception ex)
                        {
                            throw new JT808Exception(JT808ErrorCode.BodiesParseError, ex);
                        }
                    }
                }
            }
            jT808Package.End = buffer[buffer.Length - 1];
            readSize = buffer.Length;
            return jT808Package;
        }

        public int Serialize(ref byte[] bytes, int offset, JT808Package value)
        {
            // 1. 先判断是否分包（理论下发不需分包，但为了统一还是加上分包处理）
            // 2. 先序列化数据体，根据数据体的长度赋值给头部，在序列化头部。
            int messageBodyOffset = 0;
            if (value.Header.MessageBodyProperty.IsPackge)
            {   //3. 先写入分包消息总包数、包序号 
                messageBodyOffset += JT808BinaryExtensions.WriteUInt16Little(bytes, messageBodyOffset, value.Header.MessageBodyProperty.PackgeCount);
                messageBodyOffset += JT808BinaryExtensions.WriteUInt16Little(bytes, messageBodyOffset, value.Header.MessageBodyProperty.PackageIndex);
            }
            // 4. 数据体 
            Type jT808BodiesImplType = JT808MsgIdFactory.GetBodiesImplTypeByMsgId(value.Header.MsgId);
            if (jT808BodiesImplType != null)
            {
                if (value.Bodies != null)
                {
                    // 4.1 处理数据体
                    messageBodyOffset = JT808FormatterResolverExtensions.JT808DynamicSerialize(JT808FormatterExtensions.GetFormatter(jT808BodiesImplType), ref bytes, offset, value.Bodies);
                }
            }
            byte[] messageBodyBytes = null;
            if (messageBodyOffset != 0)
            {
                messageBodyBytes = new byte[messageBodyOffset];
                Array.Copy(bytes, 0, messageBodyBytes, 0, messageBodyOffset);
            }
            // ------------------------------------开始组包
            // 1.起始符
            offset += JT808BinaryExtensions.WriteByteLittle(bytes, offset, value.Begin);
            // 2.赋值头数据长度
            value.Header.MessageBodyProperty.DataLength = messageBodyOffset;
            offset = JT808FormatterExtensions.GetFormatter<JT808Header>().Serialize(ref bytes, offset, value.Header);
            if (messageBodyOffset != 0)
            {
                Array.Copy(messageBodyBytes, 0, bytes, offset, messageBodyOffset);
                offset += messageBodyOffset;
                messageBodyBytes = null;
            }
            // 4.校验码
            offset += JT808BinaryExtensions.WriteByteLittle(bytes, offset, bytes.ToXor(1, offset));
            // 5.终止符
            offset += JT808BinaryExtensions.WriteByteLittle(bytes, offset, value.End);
            return JT808Escape(ref bytes, offset);
        }

        internal static ReadOnlySpan<byte> JT808DeEscape(ReadOnlySpan<byte> buf)
        {
            var bytes = JT808ArrayPool.Rent(buf.Length);
            try
            {
                int i = 0;
                int offset = 0;
                int len = 0 + buf.Length;
                while (i < len)
                {
                    if (buf[i] == 0x7d)
                    {
                        if (len > i + 1)
                        {
                            if (buf[i + 1] == 0x01)
                            {
                                bytes[offset++] = 0x7d;
                                i++;
                            }
                            else if (buf[i + 1] == 0x02)
                            {
                                bytes[offset++] = 0x7e;
                                i++;
                            }
                            else
                            {
                                bytes[offset++] = buf[i];
                            }
                        }
                    }
                    else
                    {
                        bytes[offset++] = buf[i];
                    }
                    i++;
                }
                return bytes.AsSpan(0, offset).ToArray();
            }
            finally
            {
                JT808ArrayPool.Return(bytes);
            }
        }

        /// <summary>
        /// 转码
        /// </summary>
        /// <param name="buf"></param>
        /// <returns></returns>
        internal static int JT808Escape(ref byte[] buf,int offset)
        {
            var tmpBuffer = buf.AsSpan(0, offset).ToArray();
            int tmpOffset = 0;
            buf[tmpOffset++] = tmpBuffer[0];
            for (int i = 1; i < offset - 1; i++)
            {
                if (tmpBuffer[i] == 0x7e)
                {
                    buf[tmpOffset++] = 0x7d;
                    buf[tmpOffset++] = 0x02;
                }
                else if (tmpBuffer[i] == 0x7d)
                {
                    buf[tmpOffset++] = 0x7d;
                    buf[tmpOffset++] = 0x01;
                }
                else
                {
                    buf[tmpOffset++] = tmpBuffer[i];
                }
            }
            buf[tmpOffset++] = tmpBuffer[tmpBuffer.Length- 1];
            return tmpOffset;
        }
    }
}
