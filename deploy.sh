#!/usr/bin/env bash

# ==============================================================================
# Script tự động Deploy Website / Service bằng Docker Compose trên Ubuntu VPS
# ==============================================================================

# 1. Dừng ngay lập tức nếu có lệnh thất bại, biến chưa định nghĩa, hoặc lỗi pipeline
set -eo pipefail

# Màu sắc hiển thị cho log
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Hàm in thông báo
log_info() {
    echo -e "${BLUE}[INFO]${NC} $1"
}

log_success() {
    echo -e "${GREEN}[SUCCESS]${NC} $1"
}

log_warning() {
    echo -e "${YELLOW}[WARNING]${NC} $1"
}

log_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

# Tiêu đề bắt đầu
echo -e "${CYAN}"
echo "======================================================"
echo "          BAT DAU QUA TRINH DEPLOY WEBSITE            "
echo "======================================================"
echo -e "${NC}"

# Hàm kiểm tra quyền sudo/root khi cài đặt package
SUDO=""
if [ "$(id -u)" -ne 0 ]; then
    if command -v sudo >/dev/null 2>&1; then
        SUDO="sudo"
    else
        log_error "Script can quyen root hoac sudo de cai dat cac goi can thiet!"
        exit 1
    fi
fi

# ==============================================================================
# 2. Cấu hình Swap 4GB (tối ưu bộ nhớ ảo cho VPS)
# ==============================================================================
log_info "Kiem tra cau hinh Swap memory..."
if [ ! -f /swapfile ] && [ "$(grep -c '/swapfile' /proc/swaps 2>/dev/null || echo 0)" -eq 0 ]; then
    log_info "Chua co swapfile. Dang tien hanh tao 4GB Swap..."
    $SUDO fallocate -l 4G /swapfile 2>/dev/null || $SUDO dd if=/dev/zero of=/swapfile bs=1M count=4096 status=none
    $SUDO chmod 600 /swapfile
    $SUDO mkswap /swapfile
    $SUDO swapon /swapfile
    if ! grep -q '/swapfile' /etc/fstab; then
        echo '/swapfile none swap sw 0 0' | $SUDO tee -a /etc/fstab > /dev/null
    fi
    log_success "Khoi tao 4GB Swap thanh cong!"
elif [ -f /swapfile ] && [ "$(grep -c '/swapfile' /proc/swaps 2>/dev/null || echo 0)" -eq 0 ]; then
    log_info "File /swapfile da ton tai nhung chua duoc bat. Dang bat swap..."
    $SUDO chmod 600 /swapfile
    $SUDO swapon /swapfile || true
    log_success "Kich hoat Swap thanh cong!"
else
    log_info "Swap da duoc kich hoat tren he thong."
fi

# ==============================================================================
# 3. Kiểm tra và cài đặt Git, Docker, Docker Compose plugin nếu chưa có
# ==============================================================================
log_info "Kiem tra cac cong cu can thiet (Git, Docker, Docker Compose)..."

# 3.1. Kiem tra Git
if ! command -v git >/dev/null 2>&1; then
    log_warning "Git chua duoc cai dat. Dang tien hanh cai dat Git..."
    $SUDO apt-get update -y
    $SUDO apt-get install -y git
    log_success "Git da duoc cai dat thanh cong: $(git --version)"
else
    log_info "Git da co san: $(git --version)"
fi

# 3.2. Kiem tra Docker & Docker Compose Plugin
if ! command -v docker >/dev/null 2>&1 || ! docker compose version >/dev/null 2>&1; then
    log_warning "Docker hoac Docker Compose plugin chua duoc cai dat. Dang tien hanh cai dat..."
    
    $SUDO apt-get update -y
    $SUDO apt-get install -y ca-certificates curl gnupg lsb-release

    # Them Docker GPG key
    $SUDO install -m 0755 -d /etc/apt/keyrings
    if [ ! -f /etc/apt/keyrings/docker.gpg ]; then
        curl -fsSL https://download.docker.com/linux/ubuntu/gpg | $SUDO gpg --dearmor -o /etc/apt/keyrings/docker.gpg
        $SUDO chmod a+r /etc/apt/keyrings/docker.gpg
    fi

    # Them repository Docker vao Apt sources
    echo \
      "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu \
      $(lsb_release -cs) stable" | $SUDO tee /etc/apt/sources.list.d/docker.list > /dev/null

    $SUDO apt-get update -y
    $SUDO apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin

    # Khoi dong va bat Docker service khi boot
    $SUDO systemctl enable --now docker

    # Them user hien tai vao group docker neu chua co
    if [ -n "$USER" ] && [ "$USER" != "root" ]; then
        $SUDO usermod -aG docker "$USER" || true
    fi

    log_success "Docker va Docker Compose da duoc cai dat thanh cong!"
else
    log_info "Docker da co san: $(docker --version)"
    log_info "Docker Compose da co san: $(docker compose version)"
fi

# Dam bao Docker daemon dang chay
if ! docker info >/dev/null 2>&1; then
    log_warning "Docker daemon chua chay. Dang khoi dong Docker..."
    $SUDO systemctl start docker
fi

# ==============================================================================
# 4. Kéo code mới nhất từ Git repo (git pull origin main)
# ==============================================================================
BRANCH="${1:-main}"

if [ -d ".git" ]; then
    log_info "Dang cap nhat code tu Git repository (branch: $BRANCH)..."
    git fetch origin "$BRANCH"
    git checkout "$BRANCH"
    git pull origin "$BRANCH"
    log_success "Cap nhat code moi nhat thanh cong!"
else
    log_warning "Thu muc hien tai khong phai la Git repository. Bo qua buoc git pull."
fi

# Kiem tra file .env
if [ ! -f ".env" ] && [ -f ".env.example" ]; then
    log_warning "Khong tim thay file .env! Dang tao file .env tu .env.example..."
    cp .env.example .env
fi

# ==============================================================================
# 5. Rebuild và khởi chạy container chạy ngầm (docker compose up -d --build)
# ==============================================================================
log_info "Dang rebuild va khoi chay cac container (docker compose up -d --build)..."
docker compose up -d --build --remove-orphans
log_success "Khoi chay cac container thanh cong!"

# ==============================================================================
# 6. Dọn dẹp images/containers rác cũ (docker system prune -f)
# ==============================================================================
log_info "Dang don dep cac dangling images va container rac cu..."
docker system prune -f
log_success "Don dep he thong hoan tat!"

# ==============================================================================
# 7. In log trạng thái container
# ==============================================================================
echo ""
log_info "Trang thai hoat dong cua cac container:"
echo -e "${CYAN}"
docker compose ps
echo -e "${NC}"

echo -e "${GREEN}======================================================${NC}"
echo -e "${GREEN}             DEPLOY HOAN TAT THANH CONG!              ${NC}"
echo -e "${GREEN}======================================================${NC}"
