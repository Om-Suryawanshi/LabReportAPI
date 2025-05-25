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

    def security_test():
        tests = [
            ("VALID", b'\x02PATIENT001|GLUCOSE|120|mg/dL\x03'),  # Good
            ("SQL_INJECTION", b'\x02PATIENT001;DROP TABLE|GLUCOSE|120|mg/dL\x03'),
            ("OVERFLOW", b'\x02' + b'A'*10000 + b'\x03'),        # Buffer overflow
            ("MALFORMED", b'\x02INCOMPLETE_MESSAGE'),            # Missing ETX
            ("RAPID_FIRE", [b'\x02TEST\x03']*10)                 # Rate limit test
        ]

        for name, data in tests:
            print(f"\nTesting: {name}")
            try:
                with socket.create_connection(('127.0.0.1', 12377), timeout=2) as s:
                    if isinstance(data, list):
                        for packet in data:
                            s.sendall(packet)
                            time.sleep(0.01)
                    else:
                        s.sendall(data)
                    print(s.recv(1024))
            except Exception as e:
                print(f"Test {name} failed: {e}")
            time.sleep(1)
        
    def connect(self):
        self.sock.connect((self.host, self.port))
        print(f"Connected to {self.host}:{self.port}")
        
    def close(self):
        self.sock.send(b'\x04')  # EOT
        self.sock.close()
        print("Connection closed")

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
            print(f"Received ACK: {response.hex()}")
            time.sleep(1)
        
        client.security_test()
            
    except Exception as e:
        print(f"Error: {e}")
    finally:
        client.close()

if __name__ == "__main__":
    simulate_lab_machine()
