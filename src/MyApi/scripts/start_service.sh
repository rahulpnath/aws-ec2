#!/bin/bash
sudo systemctl daemon-reload
sudo systemctl enable myapi
sudo systemctl restart myapi
