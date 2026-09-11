#!/bin/bash

# Initial delay to ensure LiveKit starts up and binds first
sleep 10

CURRENT_IP=""

while true; do
  # Get current IP
  NEW_IP=$(curl -s https://api.ipify.org || echo "")
  
  if [ -n "$NEW_IP" ] && [ "$NEW_IP" != "$CURRENT_IP" ]; then
    if [ -n "$CURRENT_IP" ]; then
      echo "[WAN_IP_MONITOR] WAN IP changed from $CURRENT_IP to $NEW_IP. Restarting LiveKit..."
      supervisorctl restart livekit
    else
      echo "[WAN_IP_MONITOR] Initial WAN IP detected as $NEW_IP"
    fi
    CURRENT_IP=$NEW_IP
  fi
  
  # Wait for 120 seconds (2 minutes)
  sleep 120
done
