import socket
import requests
import time
import random

SERVER_IP = "192.168.0.3"
SERVER_PORT = 12377

TESTS = [
    {
        "name": "GLUCOSE",
        "unit": "mg/dL",
        "min": 70,
        "max": 140
    },
    {
        "name": "HEMOGLOBIN",
        "unit": "g/dL",
        "min": 12.0,
        "max": 17.5
    },
    {
        "name": "CHOLESTEROL",
        "unit": "mg/dL",
        "min": 120,
        "max": 240
    },
]

PATIENT_IDS = ["PATIENT001", "PATIENT002", "PATIENT003"]


def random_value(test):
    if isinstance(test['min'], float) or isinstance(test['max'], float):
        return round(random.uniform(test['min'], test['max']), 1)
    else:
        return random.randint(test['min'], test['max'])

def build_random_message(patient_id, test):
    value = random_value(test)
    msg = f"{patient_id}|{test['name']}|{value}|{test['unit']}"
    return b'\x02' + msg.encode('utf-8') + b'\x03'

def build_bad_message():
    """Create and return a set of clearly invalid test messages to send."""
    bad_messages = [
        b'\x02PATIENT999|GLUCOSE|-999|mg/dL\x03',                # Invalid patient, invalid value
        b'\x02PATIENT001|UNKNOWNTEST|123|mg/dL\x03',             # Invalid test
        b'\x02PATIENT002|GLUCOSE|9999|mg/dL\x03',                # Value out of valid range
        b'\x02PATIENT003|HEMOGLOBIN|15|mg/L\x03',                # Wrong unit
        b'\x02|GLUCOSE|100|mg/dL\x03',                           # Missing patient ID
        b'\x02PATIENT001|GLUCOSE|abc|mg/dL\x03',                 # Non-numeric value
        b'\x02PATIENT002|HEMOGLOBIN||g/dL\x03',                  # Missing value
        b'PATIENT003|CHOLESTEROL|180|mg/dL',                     # Missing STX/ETX
        b'\x02PATIENT001|GLUCOSE|120|mg/dL<script>bad()</script>\x03',  # Injection attempt
    ]
    return bad_messages

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

def send_bad_messages():
    bad_messages = build_bad_message()
    send_and_receive(bad_messages, "Sending Bad/Invalid Messages", expect_multiple=True, delay_between=0.1)

def post_manual_save():
    """Send the manual save trigger via POST request."""
    url = "http://192.168.0.3/api/labdata/save"
    try:
        resp = requests.post(url)
        print(f"POST /api/labdata/save Response: {resp.status_code} - {resp.text}")
    except Exception as e:
        print(f"Failed to send POST request: {e}")

def send_random_valid_messages(num_messages):
    """Send the specified number of random valid test messages."""
    for i in range(num_messages):
        patient_id = random.choice(PATIENT_IDS)
        test = random.choice(TESTS)
        msg = build_random_message(patient_id, test)
        send_and_receive(msg, f"Valid Randomized Message {i+1}: {patient_id} {test['name']}")
        time.sleep(0.1)

def main():
    print("Lab Data Test Tool\n")
    print("1. Send random valid test messages")
    print("2. Send invalid/bad test messages")
    print("3. Trigger manual save (POST)")
    print("4. Exit")
    while True:
        choice = input("\nEnter your choice (1-4): ").strip()
        if choice == '1':
            try:
                num = int(input("How many random valid test messages to send? "))
                assert num > 0
                send_random_valid_messages(num)
            except Exception:
                print("Please enter a valid positive integer.")
        elif choice == '2':
            send_bad_messages()
        elif choice == '3':
            post_manual_save()
        elif choice == '4':
            print("Bye!")
            break
        else:
            print("Invalid option. Try again.")

if __name__ == "__main__":
    main()
