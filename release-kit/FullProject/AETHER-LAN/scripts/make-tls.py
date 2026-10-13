import sys, ipaddress
from pathlib import Path
from datetime import datetime, timedelta, timezone
from cryptography import x509
from cryptography.x509.oid import NameOID, ExtendedKeyUsageOID
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import rsa
base=Path(sys.argv[1]);base.mkdir(exist_ok=True)
address=ipaddress.ip_address(sys.argv[2])
if not address.is_private or address.is_loopback: raise ValueError('A private guest LAN address is required')
now=datetime.now(timezone.utc)
ca_key=rsa.generate_private_key(public_exponent=65537,key_size=2048)
ca_name=x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,'AETHER LAN Windows Sandbox Test CA')])
ca=(x509.CertificateBuilder().subject_name(ca_name).issuer_name(ca_name).public_key(ca_key.public_key()).serial_number(x509.random_serial_number()).not_valid_before(now-timedelta(minutes=5)).not_valid_after(now+timedelta(days=90)).add_extension(x509.BasicConstraints(ca=True,path_length=0),critical=True).add_extension(x509.KeyUsage(False,False,False,False,False,True,True,False,False),critical=True).sign(ca_key,hashes.SHA256()))
(base/'root.cer').write_bytes(ca.public_bytes(serialization.Encoding.DER))
(base/'root.pem').write_bytes(ca.public_bytes(serialization.Encoding.PEM))
for name,ips in [('server',[address,ipaddress.ip_address('127.0.0.1')]),('wrong-host',[ipaddress.ip_address('192.0.2.123')])]:
 key=rsa.generate_private_key(public_exponent=65537,key_size=2048)
 cert=(x509.CertificateBuilder().subject_name(x509.Name([x509.NameAttribute(NameOID.COMMON_NAME,'AETHER LAN Test Relay')])).issuer_name(ca_name).public_key(key.public_key()).serial_number(x509.random_serial_number()).not_valid_before(now-timedelta(minutes=5)).not_valid_after(now+timedelta(days=90)).add_extension(x509.BasicConstraints(ca=False,path_length=None),critical=True).add_extension(x509.SubjectAlternativeName([x509.IPAddress(ip) for ip in ips]+[x509.DNSName('localhost')] if name=='server' else [x509.IPAddress(ip) for ip in ips]),critical=False).add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]),critical=False).sign(ca_key,hashes.SHA256()))
 (base/(name+'.pem')).write_bytes(cert.public_bytes(serialization.Encoding.PEM)+ca.public_bytes(serialization.Encoding.PEM))
 (base/(name+'.key')).write_bytes(key.private_bytes(serialization.Encoding.PEM,serialization.PrivateFormat.PKCS8,serialization.NoEncryption()))
print('TLS certificate created for',str(address))
