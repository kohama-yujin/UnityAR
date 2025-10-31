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
    public Transform cameraTransform;

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
    // latest camera position received from UDP (in meters)
    private Vector3 latestCameraPos = Vector3.zero;

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

                // バイト数で「ヘッダ」を判断
                if (firstPacket.Length == 12 || firstPacket.Length == 28)
                {
                    // Big-endian形式
                    uint format = ((uint)firstPacket[0] << 24) | ((uint)firstPacket[1] << 16) | ((uint)firstPacket[2] << 8) | firstPacket[3];

                    // 形式ごとに処理を分岐
                    if (format == 1)
                    {
                        // 画像フレームのヘッダ
                        uint frameId = ((uint)firstPacket[4] << 24) | ((uint)firstPacket[5] << 16) | ((uint)firstPacket[6] << 8) | firstPacket[7];
                        int numPackets = (int)(((uint)firstPacket[8] << 24) | ((uint)firstPacket[9] << 16) | ((uint)firstPacket[10] << 8) | firstPacket[11]);

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
                            uint pktFormat = ((uint)pkt[0] << 24) | ((uint)pkt[1] << 16) | ((uint)pkt[2] << 8) | pkt[3];
                            if (pktFormat != 1)
                            {
                                // 画像フレーム以外のパケットが混入した場合は無視
                                continue;
                            }
                            uint pktFrameId = ((uint)pkt[4] << 24) | ((uint)pkt[5] << 16) | ((uint)pkt[6] << 8) | pkt[7];
                            int seq = (int)(((uint)pkt[8] << 24) | ((uint)pkt[9] << 16) | ((uint)pkt[10] << 8) | pkt[11]);
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
                        // カメラ位置情報: pkt -> format(4B) + x(8B) + y(8B) + z(8B)

                        // big-endian double を読む
                        double ReadDoubleBE(byte[] buf, int off)
                        {
                            byte[] tmp = new byte[8];
                            // reverse order for BitConverter (little-endian system)
                            tmp[0] = buf[off + 7];
                            tmp[1] = buf[off + 6];
                            tmp[2] = buf[off + 5];
                            tmp[3] = buf[off + 4];
                            tmp[4] = buf[off + 3];
                            tmp[5] = buf[off + 2];
                            tmp[6] = buf[off + 1];
                            tmp[7] = buf[off + 0];
                            return BitConverter.ToDouble(tmp, 0);
                        }

                        // 座標をメートル単位で取得
                        double x = ReadDoubleBE(firstPacket, 4) / 100.0;
                        double y = ReadDoubleBE(firstPacket, 12) / 100.0;
                        double z = ReadDoubleBE(firstPacket, 20) / 100.0;

                        // Unity座標系に変換
                        double unity_x = x;
                        double unity_y = -z;
                        double unity_z = -y;

                        // Unity の Vector3 に保存（float 精度に変換）
                        lock (lockObj)
                        {
                            latestCameraPos = new Vector3((float)unity_x, (float)unity_y, (float)unity_z);
                        }

                        Debug.Log($"UdpReceiver: Received camera pos x={x:F3}m y={y:F3}m z={z:F3}m");
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
        // カメラ位置を更新
        Vector3 pos;
        lock(lockObj)
        {
            pos = latestCameraPos;
        }
        
        cameraTransform.localPosition = pos;

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