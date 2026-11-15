#!/usr/bin/env python3
"""Check that an OriCoop relay answers: sends one HostRoom request and waits for RoomCreated.

usage: python3 tools/probe.py <host> [port]      (default port 47020)
exit 0 = relay answered (prints the room code), 1 = no answer.

The test room has no players and closes by itself after about 30 s. The relay logs it like any
host (PLAYER JOIN ... steamid=unknown, then LEAVE reason="host timed out").
"""
import os
import socket
import sys

MAGIC = b"\x4f\x52"  # 0x524F, little endian
HOST_ROOM, ROOM_CREATED = 1, 3


def main():
    if len(sys.argv) < 2:
        print(__doc__.strip())
        return 2
    host = sys.argv[1]
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 47020
    nonce = os.urandom(4)
    if nonce == b"\x00\x00\x00\x00":
        nonce = b"\x01\x00\x00\x00"
    packet = MAGIC + bytes([HOST_ROOM]) + nonce
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.settimeout(2.0)
    for _ in range(3):
        sock.sendto(packet, (host, port))
        try:
            data, _addr = sock.recvfrom(2048)
        except (socket.timeout, ConnectionResetError):
            continue  # ConnectionResetError: nothing listens there (ICMP port unreachable)
        if len(data) >= 13 and data[:2] == MAGIC and data[2] == ROOM_CREATED and data[3:7] == nonce:
            print("OK relay %s:%d answered, test room %s" % (host, port, data[7:13].decode("ascii")))
            return 0
    print("FAIL no answer from %s:%d (UDP). Check the service, the OS firewall and the cloud security group." % (host, port))
    return 1


if __name__ == "__main__":
    sys.exit(main())
