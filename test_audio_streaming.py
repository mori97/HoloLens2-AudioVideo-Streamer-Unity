"""A script to test the audio streamer.
"""
import queue
import socket
import threading

import numpy as np
import matplotlib.pyplot as plt
from matplotlib.animation import FuncAnimation

PORT = 50001
HOST = '192.168.0.107'


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


class AudioReceiver(object):
    """A class to receive audio stream from a HoloLens2.

    Args:
        host (str): Hostname or IP address of a HoloLens2.
        port (int): Port number.
    """
    def __init__(self, host, port):
        self._queue = queue.Queue(maxsize=100)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.host = host
        self.port = port

    def start(self):
        """Start data receiving.
        """
        self.sock.connect((self.host, self.port))
        threading.Thread(target=self._recv_frames).start()

    def _recv_frames(self):
        while True:
            header = recv_nbytes(self.sock, 3 * 4)
            if header is None:
                break
            header = np.frombuffer(header, dtype=np.uint32)
            assert header.shape == (3,)
            n_channels, n_samples, sr = header[0], header[1], header[2]

            data = recv_nbytes(self.sock, n_channels * n_samples * 4)
            data = np.frombuffer(data, dtype=np.float32).reshape(-1, n_channels)
            self._queue.put(data)

    def get(self):
        """Get received audio data.

        Returns:
            Option[numpy.ndarray]: Received audio data of shape (n_frames, n_channels).
                `None` is returned if there is no new received data.
        """
        ret = []
        try:
            while True:
                ret.append(self._queue.get_nowait())
        except queue.Empty:
            pass

        if ret:
            ret = np.concatenate(ret, axis=0)
            return ret
        else:
            return None


class AudioPlotter(object):
    """A class to plot audio waveforms received by `AudioReceiver`.

    Args:
        receiver (AudioReceiver): An AudioReceiver object.
    """
    def __init__(self, receiver):
        self.receiver = receiver

        self._data = np.zeros((40000, 5), dtype=np.float32)
        self.figure, self.axes = plt.subplots(5, figsize=(8, 10))
        self.lines = [ax.plot(np.arange(4000 * 10), np.zeros(4000 * 10))
                      for ax in self.axes]
        for ax in self.axes:
            ax.set_ylim((-1, 1))

    def _func_animate(self, _):
        new_data = self.receiver.get()
        if new_data is None:
            return
        new_data = new_data[::12, :]  # Downsampling
        t = new_data.shape[0]
        self._data = np.roll(self._data, -t, axis=0)
        self._data[-t:, :] = new_data

        for i, line in enumerate(self.lines):
            line[0].set_ydata(self._data[:, i])

    def start(self):
        """Start plotting.
        """
        ani = FuncAnimation(self.figure,
                            self._func_animate,
                            frames=10,
                            interval=50)
        plt.show()


def main():
    receiver = AudioReceiver(HOST, PORT)
    plotter = AudioPlotter(receiver)
    receiver.start()
    plotter.start()


if __name__ == '__main__':
    main()
