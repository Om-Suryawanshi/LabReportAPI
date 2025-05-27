import socket
import time

SERVER_IP = "192.168.0.3"  # <-- change this to your server's LAN IP
SERVER_PORT = 12377

def send_and_receive(data, description, expect_multiple=False, delay_between=0.05):
    print(f"\n--- {description} ---")
    try:
        with socket.create_connection((SERVER_IP, SERVER_PORT), timeout=3) as s:
            if isinstance(data, list):
                for i, packet in enumerate(data):
                    print(f"Sending packet {i+1}: {repr(packet)}")
                    s.sendall(packet)
                    try:
                        resp = s.recv(1024)
                        print(f"Received: {repr(resp)}")
                    except socket.timeout:
                        print("No response (timeout)")
                    time.sleep(delay_between)
            else:
                print(f"Sending: {repr(data)}")
                s.sendall(data)
                try:
                    resp = s.recv(1024)
                    print(f"Received: {repr(resp)}")
                except socket.timeout:
                    print("No response (timeout)")
    except Exception as e:
        print(f"Exception: {e}")

def error():
    # 2. SQL Injection attempt
    send_and_receive(
        b'\x02PATIENT001;DROP TABLE|GLUCOSE|120|mg/dL\x03',
        "SQL Injection Attempt"
    )

    # 3. Buffer overflow (very long message)
    send_and_receive(
        b'\x02' + b'A' * 10000 + b'\x03',
        "Buffer Overflow Attempt"
    )

    # 4. Malformed message (no ETX)
    send_and_receive(
        b'\x02INCOMPLETE_MESSAGE',
        "Malformed Message (Missing ETX)"
    )

    # 5. Rapid fire (rate limiting test)
    rapid_fire_packets = [b'\x02PATIENT001|GLUCOSE|120|mg/dL\x03'] * 10
    send_and_receive(
        rapid_fire_packets,
        "Rapid Fire (Rate Limiting Test)",
        expect_multiple=True,
        delay_between=0.01
    )

    # 6. Invalid test name
    send_and_receive(
        b'\x02PATIENT004|HACKTEST|100|mg/dL\x03',
        "Invalid Test Name"
    )

    # 7. Invalid patient ID
    send_and_receive(
        b'\x02BADPATIENT|GLUCOSE|120|mg/dL\x03',
        "Invalid Patient ID"
    )

    # 8. Invalid value
    send_and_receive(
        b'\x02PATIENT005|GLUCOSE|-10|mg/dL\x03',
        "Invalid Value"
    )

    # 9. Invalid unit
    send_and_receive(
        b'\x02PATIENT006|GLUCOSE|120|mg/L\x03',
        "Invalid Unit"
    )

    # 10. HTML/JS injection attempt
    send_and_receive(
        b'\x02PATIENT007|GLUCOSE|120|mg/dL<script>alert(1)</script>\x03',
        "HTML/JS Injection Attempt"
    )

def main():
    # 1. Valid messages
    valid_messages = [
        b'\x02PATIENT001|GLUCOSE|120|mg/dL\x03',
        b'\x02PATIENT002|HEMOGLOBIN|14.5|g/dL\x03',
        b'\x02PATIENT003|CHOLESTEROL|180|mg/dL\x03',
    ]
    for msg in valid_messages:
        send_and_receive(msg, "Valid Message")

    # error()

    

if __name__ == "__main__":
    main()
