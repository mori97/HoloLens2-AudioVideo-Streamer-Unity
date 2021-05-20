"""A script to test the video streamer.
"""
import argparse
import socket
import threading

import numpy as np
import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation


def recv_nbytes(sock, n_bytes):
    """Receive exactly `n_bytes`.
    If the connection is closed, `None` will be returned.

    Args:
        sock (socket.socket): An socket object.
        n_bytes (int): Amount of data to receive.

    Returns:
        bytes: Received data.
    """
    ret = bytes()
    while len(ret) < n_bytes:
        part = sock.recv(min(4096, n_bytes - len(ret)))
        if part == '':
            return None  # the connection is closed
        ret += part
    return ret


def nv12_to_rgb(img_uv12):
    """Convert a NV12 image to the RGB format.

    Args:
        img_uv12 (numpy.ndarray): a NV12 format image array of shape (J, W).
            W is the image width and J should be 1.5x image height.

    Returns:
        numpy.ndarray: RGB format image array of shape (H, W, 3), where H is the image
            height and W is the image width.
    """
    if img_uv12.shape[0] % 3 != 0:
        raise ValueError("Invalid image size")
    height, width = img_uv12.shape[0] * 2 // 3, img_uv12.shape[1]

    y = img_uv12[:height, :].astype(np.float32) - 16
    u = img_uv12[height:, ::2]
    u = np.repeat(np.repeat(u, 2, axis=0), 2, axis=1).astype(np.float32) - 128
    v = img_uv12[height:, 1::2]
    v = np.repeat(np.repeat(v, 2, axis=0), 2, axis=1).astype(np.float32) - 128

    r = 1.164383 * y + 1.596027 * v
    g = 1.164383 * y - 0.391762 * u - 0.812968 * v
    b = 1.164383 * y + 2.017232 * u

    img_rgb = np.stack((r, g, b), axis=2)
    np.clip(img_rgb, a_min=0, a_max=255, out=img_rgb)
    return img_rgb.astype(np.uint8)


class VideoReceiver(object):
    """A class to receive video stream from a HoloLens2.

    Args:
        host (str): Hostname or IP address of a HoloLens2.
        port (int): Port number.
    """
    def __init__(self, host, port):
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.host = host
        self.port = port
        self.last_frame = None

    def start(self):
        """Start data receiving.
        """
        self.sock.connect((self.host, self.port))
        print(f"Connected to {self.host}:{self.port}")
        threading.Thread(target=self._recv_frames).start()

    def _recv_frames(self):
        while True:
            header = recv_nbytes(self.sock, 2 * 4)
            if header is None:
                break
            header = np.frombuffer(header, dtype=np.float32)
            focal_x, focal_y = header[0], header[1]
            header = recv_nbytes(self.sock, 2 * 4)
            if header is None:
                break
            header = np.frombuffer(header, dtype=np.uint32)
            width, height = header[0], header[1]

            img_nv12 = np.frombuffer(
                recv_nbytes(self.sock, width * height * 3 // 2), dtype=np.uint8
            ).reshape(height * 3 // 2, width)
            img_rgb = nv12_to_rgb(img_nv12)
            self.last_frame = img_rgb


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("host",
                        help="IP address of HoloLens2",
                        type=str)
    parser.add_argument("--port",
                        help="Port number to be used",
                        type=int, default=50002)
    args = parser.parse_args()

    receiver = VideoReceiver(args.host, args.port)
    receiver.start()

    fig, ax = plt.subplots()

    def func_animate(_):
        if receiver.last_frame is not None:
            ax.cla()
            ax.imshow(receiver.last_frame)

    ani = FuncAnimation(fig,
                        func_animate,
                        frames=10,
                        interval=50)
    plt.tight_layout()
    plt.show()


if __name__ == '__main__':
    main()
