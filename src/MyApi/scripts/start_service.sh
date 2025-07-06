#!/bin/bash
systemctl daemon-reload
systemctl enable myapi
systemctl restart myapi
