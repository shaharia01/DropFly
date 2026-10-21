package com.dropfly.app

import android.content.Context
import android.net.Uri
import android.net.wifi.WifiManager
import android.provider.OpenableColumns
import java.io.File
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.HttpURLConnection
import java.net.InetAddress
import java.net.URL

data class Target(val host: String, val port: Int, val name: String)

object Sender {
    private const val DISCOVERY_PORT = 53318

    fun prefs(ctx: Context) = ctx.getSharedPreferences("dropfly", Context.MODE_PRIVATE)

    /** Try the last known laptop first, otherwise shout on the Wi-Fi and wait for an answer. */
    fun findTarget(ctx: Context, code: String): Target? {
        val p = prefs(ctx)
        val cachedHost = p.getString("host", null)
        val cachedPort = p.getInt("port", 53317)
        if (cachedHost != null && ping(cachedHost, cachedPort, code)) {
            return Target(cachedHost, cachedPort, p.getString("name", "laptop") ?: "laptop")
        }
        val found = discover(ctx) ?: return null
        p.edit().putString("host", found.host).putInt("port", found.port).putString("name", found.name).apply()
        return found
    }

    private fun ping(host: String, port: Int, code: String): Boolean = try {
        val c = URL("http://$host:$port/ping").openConnection() as HttpURLConnection
        c.connectTimeout = 1000
        c.readTimeout = 1000
        c.setRequestProperty("X-Token", code)
        val ok = c.responseCode == 200
        c.disconnect()
        ok
    } catch (e: Exception) {
        false
    }

    private fun discover(ctx: Context): Target? {
        val msg = "DROPFLY?".toByteArray()
        val addrs = mutableListOf<InetAddress>()
        try {
            addrs.add(InetAddress.getByName("255.255.255.255"))
        } catch (e: Exception) {
        }
        try {
            @Suppress("DEPRECATION")
            val d = (ctx.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager).dhcpInfo
            val b = (d.ipAddress and d.netmask) or d.netmask.inv()
            if (d.netmask != 0) {
                addrs.add(
                    InetAddress.getByAddress(
                        byteArrayOf(b.toByte(), (b shr 8).toByte(), (b shr 16).toByte(), (b shr 24).toByte())
                    )
                )
            }
        } catch (e: Exception) {
        }

        DatagramSocket().use { s ->
            s.broadcast = true
            s.soTimeout = 700
            repeat(3) {
                for (a in addrs) {
                    try {
                        s.send(DatagramPacket(msg, msg.size, a, DISCOVERY_PORT))
                    } catch (e: Exception) {
                    }
                }
                try {
                    val buf = ByteArray(256)
                    val pkt = DatagramPacket(buf, buf.size)
                    s.receive(pkt)
                    val text = String(pkt.data, 0, pkt.length)
                    if (text.startsWith("DROPFLY!")) {
                        val parts = text.removePrefix("DROPFLY!").split("|")
                        val port = parts[0].toIntOrNull() ?: 53317
                        val name = parts.getOrNull(1) ?: "laptop"
                        return Target(pkt.address.hostAddress ?: return null, port, name)
                    }
                } catch (e: Exception) {
                }
            }
        }
        return null
    }

    fun sendFile(ctx: Context, t: Target, code: String, uri: Uri): Boolean {
        val cr = ctx.contentResolver
        var name = "file"
        var size = -1L
        try {
            cr.query(uri, null, null, null, null)?.use { c ->
                if (c.moveToFirst()) {
                    val ni = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    val si = c.getColumnIndex(OpenableColumns.SIZE)
                    if (ni >= 0) name = c.getString(ni) ?: name
                    if (si >= 0 && !c.isNull(si)) size = c.getLong(si)
                }
            }
        } catch (e: Exception) {
        }

        var tmp: File? = null
        try {
            val stream = if (size >= 0) {
                cr.openInputStream(uri) ?: return false
            } else {
                // size unknown: stage a copy so we can announce the length
                val f = File.createTempFile("up", null, ctx.cacheDir)
                tmp = f
                (cr.openInputStream(uri) ?: return false).use { i -> f.outputStream().use { o -> i.copyTo(o) } }
                size = f.length()
                f.inputStream()
            }
            val conn = URL("http://${t.host}:${t.port}/upload?name=${Uri.encode(name)}")
                .openConnection() as HttpURLConnection
            conn.requestMethod = "POST"
            conn.doOutput = true
            conn.connectTimeout = 3000
            conn.readTimeout = 120000
            conn.setFixedLengthStreamingMode(size)
            conn.setRequestProperty("X-Token", code)
            stream.use { i -> conn.outputStream.use { o -> i.copyTo(o, 64 * 1024) } }
            val ok = conn.responseCode == 200
            conn.disconnect()
            return ok
        } catch (e: Exception) {
            return false
        } finally {
            tmp?.delete()
        }
    }

    fun sendText(t: Target, code: String, text: String): Boolean = try {
        val bytes = text.toByteArray(Charsets.UTF_8)
        val conn = URL("http://${t.host}:${t.port}/text").openConnection() as HttpURLConnection
        conn.requestMethod = "POST"
        conn.doOutput = true
        conn.connectTimeout = 3000
        conn.readTimeout = 10000
        conn.setFixedLengthStreamingMode(bytes.size)
        conn.setRequestProperty("X-Token", code)
        conn.outputStream.use { it.write(bytes) }
        val ok = conn.responseCode == 200
        conn.disconnect()
        ok
    } catch (e: Exception) {
        false
    }
}
