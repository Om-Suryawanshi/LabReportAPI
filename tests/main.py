import socket
import time

class LabClient:
    def __init__(self, host='127.0.0.1', port=12377):
        self.host = host
        self.port = port
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        
    def send_message(self, message: str):
        """Send properly framed message with STX/ETX"""
        framed = b'\x02' + message.encode('utf-8') + b'\x03'
        self.sock.sendall(framed)

    def connect(self):
        self.sock.connect((self.host, self.port))
        print(f"Connected to {self.host}:{self.port}")
        
    def close(self):
        try:
            self.sock.send(b'\x04')  # Optional: Graceful EOT
        except:
            pass
        self.sock.close()
        print("Connection closed")

    def security_test(self):
        tests = [
            ("VALID", b'\x02PATIENT001|GLUCOSE|120|mg/dL\x03'),
            ("SQL_INJECTION", b'\x02PATIENT001;DROP TABLE|GLUCOSE|120|mg/dL\x03'),
            ("OVERFLOW", b'\x02' + b'A'*10000 + b'\x03'),
            ("MALFORMED", b'\x02INCOMPLETE_MESSAGE'),
            ("RAPID_FIRE", [b'\x02PATIENT001|GLUCOSE|120|mg/dL\x03'] * 10),
        ]

        for name, data in tests:
            print(f"\n--- Testing: {name} ---")
            try:
                with socket.create_connection((self.host, self.port), timeout=2) as s:
                    if isinstance(data, list):
                        for packet in data:
                            s.sendall(packet)
                            time.sleep(0.01)
                            try:
                                print("Received:", s.recv(1024))
                            except socket.timeout:
                                print("No response")
                    else:
                        s.sendall(data)
                        print("Sent:", data)
                        try:
                            response = s.recv(1024)
                            print("Received:", response)
                        except socket.timeout:
                            print("No response")
            except Exception as e:
                print(f"Test {name} failed: {e}")
            time.sleep(1)


def simulate_lab_machine():
    client = LabClient()
    
    try:
        client.connect()
        
        # Send valid messages
        tests = [
            "PATIENT123|GLUCOSE|120|mg/dL",
            "PATIENT456|HEMOGLOBIN|14.5|g/dL",
            "PATIENT789|CHOLESTEROL|180|mg/dL"
        ]
        
        for test in tests:
            print(f"Sending: {test}")
            client.send_message(test)
            
            # Wait for ACK
            response = client.sock.recv(1)
            if response == b'\x06':
                print("Received ACK")
            else:
                print(f"Unexpected response: {response.hex()}")
            time.sleep(1)
        
        client.close()
        
        # Run security tests separately
        client.security_test()
            
    except Exception as e:
        print(f"Error: {e}")
    finally:
        try:
            client.close()
        except:
            pass

if __name__ == "__main__":
    simulate_lab_machine()
