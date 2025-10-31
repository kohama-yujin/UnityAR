using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class UdpReceiver : MonoBehaviour
{
    public int listenPort = 12345;
    public Renderer targetRenderer; // PlaneなどのRendererをInspectorで指定
    public Camera targetCamera;

    // タイムアウト等の設定（必要に応じ調整）
    public int socketReceiveTimeoutMs = 2000;      // UDP受信ソケットのブロックタイムアウト
    public int frameReceiveTimeoutMs = 1500;       // 1フレーム分のチャンク受信待ち時間

    private UdpClient udpClient;
    private IPEndPoint remoteEP;
    private Thread receiveThread;
    private volatile bool running = false;

    // 受信バッファ（スレッド間の受け渡し）
    private Queue<byte[]> receivedImages = new Queue<byte[]>();
    private object lockObj = new object();

    // キャッシュ（Texture / Material）を使ってGCとマテリアル複製を抑える
    private Texture2D tex;
    private Material cachedMaterial;

    // 垂直視野角
    private float latestVerticalFov = 0f;
    // Unityの座標系
    private Matrix4x4 worldShift = new Matrix4x4();
    // PythonからUnityへの変換行列
    private Matrix4x4 ConvertPyToUnity = Matrix4x4.identity;

    // 最新のカメラ位置
    private Vector3 latestCameraPos = Vector3.zero;
    // 最新のカメラ回転
    private Matrix4x4 latestCameraRot = Matrix4x4.identity;

    void Start()
    {
        udpClient = new UdpClient(listenPort);
        udpClient.Client.ReceiveTimeout = socketReceiveTimeoutMs;
        remoteEP = new IPEndPoint(IPAddress.Any, 0);

        running = true;
        receiveThread = new Thread(ReceiveLoop);
        receiveThread.IsBackground = true;
        receiveThread.Start();

        if (targetRenderer == null)
            Debug.LogWarning("UdpReceiver: targetRenderer is not assigned.");
        else
        {
            // マテリアルをキャッシュして毎フレームの material プロパティ参照を避ける
            cachedMaterial = targetRenderer.material;
        }
        
        if (targetCamera == null)
        {
            Debug.LogError("targetCamera が設定されていません。");
        }
        
        worldShift.SetRow(0, new Vector4(0f, 1f, 0f, 0f));
        worldShift.SetRow(1, new Vector4(0f, 0f, 1f, 0f));
        worldShift.SetRow(2, new Vector4(-1f, 0f, 0f, 0f));
        worldShift.SetRow(3, new Vector4(0f, 0f, 0f, 1f));

        Debug.Log($"UdpReceiver: listening on port {listenPort}");
    }

    void ReceiveLoop()
    {
        try
        {
            while (running)
            {
                byte[] firstPacket = null;
                try
                {
                    // ヘッダを受信
                    firstPacket = udpClient.Receive(ref remoteEP);
                }
                catch (SocketException)
                {
                    // タイムアウトやソケットエラーはループ継続
                    continue;
                }

                if (firstPacket == null || firstPacket.Length == 0) continue;

                // バイト数でフォーマットを判定
                if (firstPacket.Length == 12 || firstPacket.Length == 44 || firstPacket.Length == 52)
                {
                    // Big-endian形式
                    uint format = EndianConverter.ReadUInt32BE(firstPacket, 0);

                    // 形式ごとに処理を分岐
                    if (format == 1)
                    {
                        // 画像フレームのヘッダ
                        uint frameId = EndianConverter.ReadUInt32BE(firstPacket, 4);
                        int numPackets = (int)EndianConverter.ReadUInt32BE(firstPacket, 8);

                        if (numPackets <= 0)
                            continue;

                        // パケット番号（seq）をキーに、データ（byte[]）を入れる箱
                        var chunks = new Dictionary<int, byte[]>();
                        // タイムアウト管理
                        var sw = System.Diagnostics.Stopwatch.StartNew();

                        while (chunks.Count < numPackets && sw.ElapsedMilliseconds < frameReceiveTimeoutMs)
                        {
                            byte[] pkt = null;
                            try
                            {
                                // 1つのパケット（チャンク）を受信
                                pkt = udpClient.Receive(ref remoteEP);
                            }
                            catch (SocketException)
                            {
                                // タイムアウト等で一度受け取れなければループを継続して再試行
                                continue;
                            }
                            
                            // 12バイト（format + frame_id + seq）に満たない場合は無視
                            if (pkt == null || pkt.Length < 12) continue;

                            // パケットの最初の12B
                            // pkt: format(4B) + frame_id(4B) + seq(4B) + chunk
                            uint pktFormat = EndianConverter.ReadUInt32BE(pkt, 0);
                            if (pktFormat != 1)
                            {
                                // 画像フレーム以外のパケットが混入した場合は無視
                                continue;
                            }
                            uint pktFrameId = EndianConverter.ReadUInt32BE(pkt, 4);
                            int seq = EndianConverter.ReadInt32BE(pkt, 8);
                            if (pktFrameId != frameId) 
                            {
                                // 遅れて届いた前のフレームのパケット（遅延到着など）なら無視
                                continue;
                            }

                            // 残りのデータ部分（画像データの一部）をコピー
                            int chunkLen = pkt.Length - 12;
                            var chunk = new byte[chunkLen];
                            Array.Copy(pkt, 12, chunk, 0, chunkLen);

                            // 同じパケット番号を2回受け取っても、1回しか登録しない
                            if (!chunks.ContainsKey(seq))
                            {
                                chunks[seq] = chunk;
                            }
                        }

                        // 必要な個数揃ったか確認
                        if (chunks.Count == numPackets)
                        {
                            int total = 0;
                            // 欠けてるチャンクがないか確認しつつ、合計サイズを求める
                            for (int i = 0; i < numPackets; i++)
                            {
                                if (!chunks.ContainsKey(i)) { total = -1; break; }
                                total += chunks[i].Length;
                            }
                            // 正しい順番（seq順）に全チャンクを結合
                            if (total > 0)
                            {
                                var all = new byte[total];
                                int pos = 0;
                                for (int i = 0; i < numPackets; i++)
                                {
                                    var c = chunks[i];
                                    Array.Copy(c, 0, all, pos, c.Length);
                                    pos += c.Length;
                                }
                                // マルチスレッドで安全に扱うため lock を使って排他制御
                                lock (lockObj)
                                {
                                    // キューは最新1枚だけ保持する方針
                                    receivedImages.Clear();
                                    receivedImages.Enqueue(all);
                                }
                            }
                        }
                        else
                        {
                            // 欠損があるので破棄して次フレームへ（同期を取るため）
                            continue;
                        }
                    }
                    else if (format == 2)
                    {
                        // カメラの初期パラメータ（座標軸と垂直視野角）
                        float[] values = new float[9];
                        for (int i = 0; i < 9; i++)
                            values[i] = EndianConverter.ReadFloatBE(firstPacket, 4 + i * 4);

                        // カメラの座標軸
                        Matrix4x4 camShift = Matrix4x4.identity;
                        camShift.m00 = values[0]; camShift.m01 = values[1]; camShift.m02 = values[2];
                        camShift.m10 = values[3]; camShift.m11 = values[4]; camShift.m12 = values[5];
                        camShift.m20 = values[6]; camShift.m21 = values[7]; camShift.m22 = values[8];

                        // 座標変換行列
                        ConvertPyToUnity = worldShift * camShift;

                        lock (lockObj)
                        {
                            // 垂直視野角
                            latestVerticalFov = EndianConverter.ReadFloatBE(firstPacket, 40);
                        }
                    }
                    else if (format == 3)
                    {
                        // カメラの外部パラメータ
                        float[] values = new float[12];
                        for (int i = 0; i < 12; i++)
                            values[i] = EndianConverter.ReadFloatBE(firstPacket, 4 + i * 4);

                        // 回転行列
                        Matrix4x4 R = Matrix4x4.identity;
                        R.m00 = values[0]; R.m01 = values[1]; R.m02 = values[2];
                        R.m10 = values[3]; R.m11 = values[4]; R.m12 = values[5];
                        R.m20 = values[6]; R.m21 = values[7]; R.m22 = values[8];

                        // 位置
                        Vector3 t = new Vector3(
                            values[9] / 100,
                            values[10] / 100,
                            values[11] / 100
                        );

                        // Unity座標系に変換（右手→左手）
                        lock (lockObj)
                        {
                            latestCameraRot = ConvertPyToUnity * R * ConvertPyToUnity.transpose;
                            latestCameraPos = ConvertPyToUnity.MultiplyPoint(t);
                        }

                        Debug.Log($"{latestCameraPos}");
                        Debug.Log($"{latestCameraRot}");
                    }
                    else
                    {
                        // 未知のフォーマット。無視して次へ。
                        continue;
                    }
                }
                else
                {
                    // header ではないパケットが来た：古いチャンクや乱入パケット。無視して次へ。
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            UnityEngine.Debug.LogError("UdpReceiver.ReceiveLoop exception: " + ex);
        }
    }

    void Update()
    {
        // 視野角を更新
        float v_fov;
        lock(lockObj)
        {
            v_fov = latestVerticalFov;
        }
        targetCamera.fieldOfView = v_fov;

        // カメラ位置を更新
        Vector3 pos;
        Matrix4x4 rot;
        lock(lockObj)
        {
            pos = latestCameraPos;
            rot = latestCameraRot;
        }        
        targetCamera.transform.localPosition = pos;
        targetCamera.transform.localRotation = rot.rotation;

        // 受信キューから最新の画像データを取り出す
        byte[] img = null;
        lock (lockObj)
        {
            if (receivedImages.Count > 0)
            {
                img = receivedImages.Dequeue();
                // 直前の古いものは捨てる（Queueに複数入っていることは想定しない）
                receivedImages.Clear();
            }
        }

        if (img == null) return;

        try
        {
            if (tex == null) tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);

            if (tex.LoadImage(img))
            {
                if (cachedMaterial != null)
                {
                    cachedMaterial.mainTexture = tex;
                }
                else if (targetRenderer != null)
                {
                    // 最後の手段で直接セット（通常は cachedMaterial を使う）
                    targetRenderer.material.mainTexture = tex;
                }
            }
            else
            {
                Debug.LogWarning("UdpReceiver: Failed to decode image.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError("UdpReceiver.Update decode error: " + ex);
        }
    }

    void OnApplicationQuit()
    {
        running = false;
        try
        {
            udpClient?.Close();
        }
        catch { }
        try
        {
            if (receiveThread != null && receiveThread.IsAlive)
            {
                receiveThread.Join(500);
            }
        }
        catch { }
    }
}